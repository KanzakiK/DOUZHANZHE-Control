using System.Text.Json;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public class BackgroundRotationService : BackgroundService
{
    private sealed class BackgroundOpts
    {
        public bool Enabled { get; set; }
        public string Source { get; set; } = "local";
        public string Interval { get; set; } = "1h";
        public string ApiUrl { get; set; } = "";
    }

    private readonly HttpClient _http = new();
    private readonly string _configDir;
    private readonly string _optsPath;
    private DateTime _lastRotation = DateTime.MinValue;
    private string _configSignature = "";
    private int _urlIndex;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public BackgroundRotationService(string configDir)
    {
        _configDir = configDir;
        _optsPath = Path.Combine(configDir, "background-opts.json");
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DouzhanzheConsole-BackgroundRotation/1.0");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 等 API 完全启动后再做首次轮换
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var opts = ReadOpts();
                var signature = $"{opts.Enabled}|{opts.Source}|{opts.Interval}|{opts.ApiUrl}";
                var intervalSeconds = ParseInterval(opts.Interval);
                var now = DateTime.UtcNow;

                if (opts.Enabled && opts.Source == "network" && !string.IsNullOrWhiteSpace(opts.ApiUrl))
                {
                    var due = now - _lastRotation >= TimeSpan.FromSeconds(intervalSeconds);
                    if (signature != _configSignature || due)
                    {
                        await _busy.WaitAsync(ct);
                        try { await RotateOnceAsync(opts.ApiUrl, ct); }
                        finally { _busy.Release(); }
                    }
                }

                _configSignature = signature;
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Write("Background", $"轮换服务异常: {ex.Message}");
                try { await Task.Delay(10_000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private BackgroundOpts ReadOpts()
    {
        var opts = new BackgroundOpts();
        if (!File.Exists(_optsPath)) return opts;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_optsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True)
                opts.Enabled = true;
            if (root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
                opts.Source = s.GetString() ?? "local";
            if (root.TryGetProperty("interval", out var i) && i.ValueKind == JsonValueKind.String)
                opts.Interval = i.GetString() ?? "1h";
            if (root.TryGetProperty("apiUrl", out var u) && u.ValueKind == JsonValueKind.String)
                opts.ApiUrl = u.GetString() ?? "";
        }
        catch { /* 配置损坏时按默认处理 */ }
        return opts;
    }

    private static int ParseInterval(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval)) return 3600;
        var s = interval.Trim().ToLowerInvariant();
        return s switch
        {
            "30m" => 1800,
            "3h" => 10800,
            "1d" => 86400,
            _ when s.EndsWith("h") && int.TryParse(s[..^1], out var h) => h * 3600,
            _ when s.EndsWith("m") && int.TryParse(s[..^1], out var m) => m * 60,
            _ when s.EndsWith("s") && int.TryParse(s[..^1], out var sec) => Math.Max(10, sec),
            _ => 3600, // 默认 1h
        };
    }

    private async Task<string> RotateOnceAsync(string apiUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            return "error:未配置 API 地址";

        try
        {
            using var resp = await _http.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            var media = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";

            string imageUrl;
            byte[] bytes;

            if (media.StartsWith("image/"))
            {
                // 接口直接返回图片（可能经过 302 跳转到图片地址），使用最终地址
                imageUrl = resp.RequestMessage?.RequestUri?.ToString() ?? apiUrl;
                bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            }
            else
            {
                // 按 JSON 解析：{ urls: [...] } / 顶层数组 / 含 url 字段的对象
                var json = await resp.Content.ReadAsStringAsync(ct);
                var urls = ParseImageUrls(json);
                if (urls.Count == 0)
                    return "error:接口未返回可识别的图片地址（需为 JSON 列表/对象，或图片直链）";
                var pick = urls[_urlIndex % urls.Count];
                _urlIndex++;
                imageUrl = pick;

                using var imgResp = await _http.GetAsync(pick, HttpCompletionOption.ResponseHeadersRead, ct);
                imgResp.EnsureSuccessStatusCode();
                media = imgResp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                bytes = await imgResp.Content.ReadAsByteArrayAsync(ct);
            }

            if (bytes.Length == 0)
                return "error:图片内容为空";
            if (bytes.Length > 20 * 1024 * 1024)
                return "error:图片超过 20MB 限制";

            var ext = DetectExt(media, imageUrl);
            await ReplaceImageAsync(bytes, ext, imageUrl, ct);
            _lastRotation = DateTime.UtcNow;
            AppLog.Write("Background", $"网络轮换: ✓ {ext} ← {imageUrl}");
            return "ok:" + imageUrl;
        }
        catch (Exception ex)
        {
            AppLog.Write("Background", $"网络轮换失败: {ex.Message}");
            return "error:" + ex.Message;
        }
    }

    /// <summary>
    /// 手动立即轮换一次：前端「立刻切换」按钮调用。可传入临时 URL 覆盖已保存配置。
    /// 返回 "ok:<图片地址>" 或 "error:<原因>"。
    /// </summary>
    public async Task<string> RotateNowAsync(string? requestedUrl, CancellationToken ct)
    {
        var opts = ReadOpts();
        var url = string.IsNullOrWhiteSpace(requestedUrl) ? opts.ApiUrl : requestedUrl!;
        await _busy.WaitAsync(ct);
        try
        {
            return await RotateOnceAsync(url, ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    private static List<string> ParseImageUrls(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            void Collect(JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray()) Collect(item);
                }
                else if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && IsHttpUrl(s)) urls.Add(s);
                }
                else if (el.ValueKind == JsonValueKind.Object
                         && el.TryGetProperty("url", out var u)
                         && u.ValueKind == JsonValueKind.String)
                {
                    var s = u.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && IsHttpUrl(s)) urls.Add(s);
                }
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("urls", out var arr))
                Collect(arr);
            else
                Collect(root);
        }
        catch { /* 非 JSON（或纯文本）时不作为图片列表 */ }
        return urls;
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string DetectExt(string media, string pick)
    {
        if (string.IsNullOrEmpty(media)) media = "";
        else media = media.ToLowerInvariant();
        if (media.Contains("png")) return ".png";
        if (media.Contains("jpeg") || media.Contains("jpg")) return ".jpg";
        if (media.Contains("webp")) return ".webp";

        var path = Uri.TryCreate(pick, UriKind.Absolute, out var u) ? u.AbsolutePath.ToLowerInvariant() : "";
        if (path.EndsWith(".png")) return ".png";
        if (path.EndsWith(".jpg") || path.EndsWith(".jpeg")) return ".jpg";
        if (path.EndsWith(".webp")) return ".webp";
        return ".png";
    }

    private async Task ReplaceImageAsync(byte[] bytes, string ext, string sourceUrl, CancellationToken ct)
    {
        var filePath = Path.Combine(_configDir, "background" + ext);
        var tmpPath = filePath + ".tmp";
        await File.WriteAllBytesAsync(tmpPath, bytes, ct);

        try
        {
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Write("Background", $"网络轮换: 替换图片失败 {ex.Message}，保留当前图");
            try { File.Delete(tmpPath); } catch { }
            return;
        }

        // 清理其它扩展名的旧图，避免 /api/background 取到过期文件
        foreach (var old in Directory.GetFiles(_configDir, "background.*"))
        {
            if (old.Equals(filePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsImageFile(old))
            {
                try { File.Delete(old); } catch { }
            }
        }

        AppLog.Write("Background", $"网络轮换: ✓ {Path.GetFileName(filePath)} ← {sourceUrl}");
    }

    private static bool IsImageFile(string path)
    {
        return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
