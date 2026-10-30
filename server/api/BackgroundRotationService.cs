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

            string? imageUrl = null;
            byte[]? bytes = null;

            if (media.StartsWith("image/"))
            {
                // 接口直接返回图片（可能经过 302 跳转到图片地址），响应体即是图片
                imageUrl = resp.RequestMessage?.RequestUri?.ToString() ?? apiUrl;
                bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            }
            else
            {
                // 非图片响应：按 JSON / 纯文本收集候选图片地址，再逐个校验 Content-Type
                var body = await resp.Content.ReadAsStringAsync(ct);
                var candidates = CollectImageCandidates(body);
                if (candidates.Count == 0)
                {
                    var trimmed = body.Trim();
                    if (IsHttpUrl(trimmed)) candidates.Add(trimmed);
                }
                if (candidates.Count == 0)
                    return "error:接口未返回可识别的图片地址（支持 JSON / 图片直链 / 文本 URL）";

                int n = candidates.Count;
                int start = _urlIndex % n;
                bool ok = false;
                for (int k = 0; k < n && !ok; k++)
                {
                    var cand = candidates[(start + k) % n];
                    if (cand.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        var comma = cand.IndexOf(',');
                        if (comma <= 0) continue;
                        try
                        {
                            var meta = cand.Substring(0, comma).ToLowerInvariant();
                            bytes = Convert.FromBase64String(cand.Substring(comma + 1));
                            media = meta.Contains("png") ? "image/png" : meta.Contains("webp") ? "image/webp" : "image/jpeg";
                            imageUrl = cand;
                            ok = true;
                        }
                        catch { /* 跳过损坏的 data URL，尝试下一个候选 */ }
                        continue;
                    }
                    try
                    {
                        using var imgResp = await _http.GetAsync(cand, HttpCompletionOption.ResponseHeadersRead, ct);
                        var cMedia = imgResp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                        if (!cMedia.StartsWith("image/")) continue; // 非图片，尝试下一个候选
                        imageUrl = imgResp.RequestMessage?.RequestUri?.ToString() ?? cand;
                        media = cMedia;
                        bytes = await imgResp.Content.ReadAsByteArrayAsync(ct);
                        ok = true;
                    }
                    catch { /* 候选不可达，尝试下一个 */ }
                }
                if (!ok) return "error:候选地址均不是有效图片";
                _urlIndex++;
            }

            if (bytes == null || bytes.Length == 0)
                return "error:图片内容为空";
            if (bytes.Length > 20 * 1024 * 1024)
                return "error:图片超过 20MB 限制";

            var ext = DetectExt(media, imageUrl ?? apiUrl);
            await ReplaceImageAsync(bytes, ext, imageUrl ?? apiUrl, ct);
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

    // 更可能是图片地址的字段名（优先尝试）
    private static readonly HashSet<string> ImageUrlKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "src", "image", "img", "pic", "picture", "link",
        "image_url", "img_url", "cover", "thumbnail", "origin", "original"
    };

    // 从 JSON / 纯文本响应中收集所有候选图片地址，按优先级排序：
    // 命名字段(url/src/...) > data:image 内嵌 > 其它 http(s) 字符串
    private static List<string> CollectImageCandidates(string body)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return result;
        try
        {
            using var doc = JsonDocument.Parse(body);
            WalkJson(doc.RootElement, result);
        }
        catch { /* 非 JSON：当作纯文本在调用方处理 */ }
        return result;
    }

    private static void WalkJson(JsonElement el, List<string> outList)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) AddCandidate(s, outList, hint: null);
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (ImageUrlKeys.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                        AddCandidate(prop.Value.GetString(), outList, hint: prop.Name);
                    else
                        WalkJson(prop.Value, outList);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) WalkJson(item, outList);
                break;
        }
    }

    private static void AddCandidate(string? s, List<string> outList, string? hint)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        if (s.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            outList.Insert(0, s); // 内嵌图优先
            return;
        }
        if (!IsHttpUrl(s)) return;
        if (hint != null) outList.Insert(0, s); // 命中图片字段优先
        else outList.Add(s);
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
