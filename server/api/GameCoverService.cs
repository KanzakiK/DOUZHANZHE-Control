// SPDX-License-Identifier: GPL-3.0-only
//
// GameCoverService — 游戏卡片封面下载/管理
// ==========================================
// 图源优先级:
//   1. SteamGridDB (需用户免费 API Key, 存 config/cover-opts.json, 绝不下发前端)
//      steam  → /grids/steam/{appid}    精确匹配（扫描/回填拿到 AppID, 零搜索零误配）
//      epic   → /grids/epic/{catalogId} → 失败落名称搜索
//      manual → 名称搜索（仅编辑弹窗单发调用; 批量补齐跳过 manual 防误配）
//   2. Steam 官方 CDN 直链兜底（仅 steam 来源, 无需 Key）:
//      https://shared.steamstatic.com/store_item_assets/steam/apps/{appid}/library_600x900.jpg
// 存储: config/covers/{profileId}.{ext}
//       hasCover 由目录扫描判定（无持久化字段, 旧 game-profiles.json 零迁移）
// 队列: Channel + 2 并发消费; SGDB API 请求间隔 300ms; 429 延迟 30s 重试一次
// 设计文档: docs/plans/game-cover-steamgriddb.md

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public sealed record SgdbGame(long Id, string Name, bool Verified);
public sealed record SgdbGrid(long Id, int Score, string Url, string Thumb, string Style);
public sealed record CoverFailure(string ProfileId, string Name, string Error);
public sealed record CoverProgress(int Total, int Done, int Failed, int Running, List<CoverFailure> Failures);

/// <summary>cover-opts.json 模型。public：STJ 反序列化要求 public 无参构造（嵌套 private 类会被静默失败）。</summary>
public sealed class CoverOpts { public string SgdbKey { get; set; } = ""; }

public sealed class GameCoverService : BackgroundService
{
    private sealed record CoverJob(string ProfileId, bool Force);

    private const int MaxImageBytes = 20 * 1024 * 1024;   // 远程下载上限
    private const int MaxUploadBytes = 8 * 1024 * 1024;   // 本地上传上限（对齐壁纸）

    private readonly GameProfileService _profiles;
    private readonly string _coversDir;
    private readonly string _optsPath;
    private readonly HttpClient _http;   // 图片下载（SGDB CDN / Steam CDN）
    private readonly HttpClient _sgdb;   // SGDB API（Bearer）

    private readonly Channel<CoverJob> _queue = Channel.CreateUnbounded<CoverJob>();
    private readonly SemaphoreSlim _gate = new(2, 2);                       // 全局并发 2
    private readonly ConcurrentDictionary<string, byte> _queued = new();    // 防重复入队
    private readonly ConcurrentDictionary<string, string> _failures = new(); // profileId → 原因
    private readonly object _optsLock = new();

    private int _total, _done, _failed, _running;

    public GameCoverService(string configDir, GameProfileService profiles)
    {
        _profiles = profiles;
        _coversDir = Path.Combine(configDir, "covers");
        Directory.CreateDirectory(_coversDir);
        _optsPath = Path.Combine(configDir, "cover-opts.json");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DouzhanzheConsole-CoverFetch/1.0");

        _sgdb = new HttpClient { Timeout = TimeSpan.FromSeconds(12), BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/") };
        _sgdb.DefaultRequestHeaders.UserAgent.ParseAdd("DouzhanzheConsole-CoverFetch/1.0");
    }

    // ── 后台消费 ─────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动收尾: 清理无主封面 + 存量 StoreAppId 回填
        CleanupOrphans();
        try
        {
            GameScannerService.BackfillStoreAppIds(_profiles);
        }
        catch (Exception ex)
        {
            AppLog.Write("GameCover", $"存量 StoreAppId 回填失败: {ex.Message}");
        }

        // 2 并发消费，单个任务异常绝不拖垮队列
        var consumers = new[] { ConsumeLoop(stoppingToken), ConsumeLoop(stoppingToken) };
        try { await Task.WhenAll(consumers); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("GameCover", $"消费循环退出: {ex.Message}"); }
    }

    private async Task ConsumeLoop(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            Interlocked.Increment(ref _running);
            string? err = null;
            try
            {
                err = await FetchOneAsync(job, ct);
            }
            catch (OperationCanceledException) { err = "已取消"; }
            catch (Exception ex) { err = ex.Message; }
            finally
            {
                Interlocked.Decrement(ref _running);
                _queued.TryRemove(job.ProfileId, out _);
            }

            if (err == null)
            {
                Interlocked.Increment(ref _done);
                _failures.TryRemove(job.ProfileId, out _);
            }
            else
            {
                Interlocked.Increment(ref _failed);
                _failures[job.ProfileId] = err;
                var name = _profiles.GetById(job.ProfileId)?.Name ?? job.ProfileId;
                AppLog.Write("GameCover", $"封面获取失败 [{name}]: {err}");
            }
        }
    }

    /// <summary>单张封面抓取。返回 null=成功，其它=失败原因。</summary>
    private async Task<string?> FetchOneAsync(CoverJob job, CancellationToken ct)
    {
        var p = _profiles.GetById(job.ProfileId);
        if (p == null) return "规则已删除";
        if (!string.IsNullOrEmpty(p.ExePath) && !File.Exists(p.ExePath))
            return "游戏文件不存在（可能已卸载）";
        if (!job.Force && HasCover(job.ProfileId)) return null; // 幂等跳过

        var key = GetSgdbKey();
        string? sgdbErr;
        if (key != null)
        {
            await _gate.WaitAsync(ct);
            try { sgdbErr = await TryFetchViaSgdbAsync(p, key, ct); }
            finally { _gate.Release(); }
            if (sgdbErr == null) return null;
            AppLog.Write("GameCover", $"SGDB 失败 [{p.Name}]: {sgdbErr}，尝试兜底");
        }
        else
        {
            sgdbErr = "未配置 SGDB Key";
        }

        // 兜底: Steam 官方 CDN 直链（无需鉴权）
        if (p.Source == "steam" && !string.IsNullOrEmpty(p.StoreAppId))
        {
            var url = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{p.StoreAppId}/library_600x900.jpg";
            var dlErr = await DownloadToFileAsync(url, p.Id, ct);
            if (dlErr == null)
            {
                AppLog.Write("GameCover", $"官方直链兜底成功 [{p.Name}]");
                return null;
            }
            return $"{sgdbErr}; 兜底失败: {dlErr}";
        }

        return sgdbErr;
    }

    /// <summary>SGDB 链路: ID 精确匹配 → 名称搜索（缓存优先）。返回 null=成功。</summary>
    private async Task<string?> TryFetchViaSgdbAsync(GameProfile p, string key, CancellationToken ct)
    {
        // ① 商店 ID 精确匹配（404/无封面 → 落名称搜索；鉴权/网络错误 → 直接返回，外层 steam 落官方直链）
        if (!string.IsNullOrEmpty(p.StoreAppId) && (p.Source == "steam" || p.Source == "epic"))
        {
            var idPath = p.Source == "steam" ? $"grids/steam/{p.StoreAppId}" : $"grids/epic/{p.StoreAppId}";
            var g = await GridsBestAsync(idPath, key, ct);
            if (g.Error != null) return g.Error;
            if (g.Url != null) return await SaveCoverAsync(g.Url, p, gameId: null, ct);
            // ID 未命中（如 SGDB 的 epic ID 体系与 CatalogItemId 不一致）→ 落名称搜索
        }

        // ② 名称搜索（SgdbGameId 缓存优先）
        string? gameId;
        if (!string.IsNullOrEmpty(p.SgdbGameId))
        {
            gameId = p.SgdbGameId;
        }
        else
        {
            (gameId, var searchErr) = await SearchGameIdAsync(p.Name, key, ct);
            if (gameId == null) return searchErr ?? "SGDB 未找到匹配游戏";
        }

        var best = await GridsBestAsync($"grids/game/{gameId}", key, ct);
        if (best.Error != null) return best.Error;
        if (best.Url == null) return "SGDB 该游戏无可用竖版封面";
        return await SaveCoverAsync(best.Url, p, gameId, ct);
    }

    private async Task<string?> SaveCoverAsync(string url, GameProfile p, string? gameId, CancellationToken ct)
    {
        var dlErr = await DownloadToFileAsync(url, p.Id, ct);
        if (dlErr != null) return $"封面下载失败: {dlErr}";
        if (gameId != null && p.SgdbGameId != gameId)
            _profiles.UpdateCoverMeta(p.Id, sgdbGameId: gameId); // 写回匹配缓存
        AppLog.Write("GameCover", $"封面已保存 [{p.Name}] ← {url}");
        return null;
    }

    // ── SGDB API ─────────────────────────────────────────────

    private sealed class GridResult { public string? Url; public string? Error; }

    /// <summary>取该游戏 600x900 静态封面中 score 最高的一张的下载地址。</summary>
    private async Task<GridResult> GridsBestAsync(string path, string key, CancellationToken ct)
    {
        var (list, err) = await GridsListAsync(path, key, ct);
        if (err != null) return new GridResult { Error = err };
        var best = list!.OrderByDescending(g => g.Score).FirstOrDefault();
        return new GridResult { Url = best?.Url };
    }

    private async Task<(List<SgdbGrid>? Data, string? Error)> GridsListAsync(string path, string key, CancellationToken ct)
    {
        await Task.Delay(300, ct); // SGDB API 限速
        try
        {
            using var res = await SendSgdbAsync(path + "?dimensions=600x900&types=static", key, ct);
            if ((int)res.StatusCode == 401) return (null, "SGDB Key 无效");
            // 404 = 游戏/ID 不在 SGDB：视为"未命中"返回空列表，由调用方落名称搜索
            if ((int)res.StatusCode == 404) return (new List<SgdbGrid>(), null);
            if ((int)res.StatusCode == 429)
            {
                // 限流: 延迟 30s 重试一次
                await Task.Delay(30_000, ct);
                using var res2 = await SendSgdbAsync(path + "?dimensions=600x900&types=static", key, ct);
                if (!res2.IsSuccessStatusCode) return (null, $"SGDB 限流且重试失败 ({(int)res2.StatusCode})");
                return await ParseGridsAsync(res2, ct);
            }
            if (!res.IsSuccessStatusCode) return (null, $"SGDB 返回 {(int)res.StatusCode}");
            return await ParseGridsAsync(res, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, $"SGDB 请求失败: {ex.Message}"); }
    }

    private async Task<HttpResponseMessage> SendSgdbAsync(string path, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await _sgdb.SendAsync(req, ct);
    }

    private static async Task<(List<SgdbGrid>? Data, string? Error)> ParseGridsAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(), cancellationToken: ct);
            var list = new List<SgdbGrid>();
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in arr.EnumerateArray())
                {
                    var url = g.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(url)) continue;
                    list.Add(new SgdbGrid(
                        g.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64() : 0,
                        g.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
                        url,
                        g.TryGetProperty("thumb", out var t) ? t.GetString() ?? "" : "",
                        g.TryGetProperty("style", out var st) ? st.GetString() ?? "" : ""));
                }
            }
            return (list, null);
        }
        catch (Exception ex) { return (null, $"SGDB 响应解析失败: {ex.Message}"); }
    }

    /// <summary>名称搜索 → 候选游戏列表（编辑弹窗搜索用，公开代理）。</summary>
    public async Task<(List<SgdbGame>? Data, string? Error)> SearchGamesAsync(string term, CancellationToken ct = default)
    {
        var key = GetSgdbKey();
        if (key == null) return (null, "未配置 SteamGridDB API Key");
        return await SearchCoreAsync(term, key, ct);
    }

    private async Task<(List<SgdbGame>? Data, string? Error)> SearchCoreAsync(string term, string key, CancellationToken ct)
    {
        term = NormalizeSearchTerm(term);
        await Task.Delay(300, ct); // SGDB API 限速
        try
        {
            using var res = await SendSgdbAsync("search/autocomplete/" + Uri.EscapeDataString(term), key, ct);
            if ((int)res.StatusCode == 401) return (null, "SGDB Key 无效");
            if (!res.IsSuccessStatusCode) return (null, $"SGDB 返回 {(int)res.StatusCode}");
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(), cancellationToken: ct);
            var list = new List<SgdbGame>();
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in arr.EnumerateArray())
                {
                    list.Add(new SgdbGame(
                        g.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64() : 0,
                        g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        g.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True));
                }
            }
            // 客户端重排：SGDB autocomplete 条数有限且把系列页/热门作品排前面，
            // 目标游戏可能被挤出可视范围。按与搜索词的 token 覆盖度重排（全等 > 覆盖率 > verified）。
            var tokens = Tokenize(term).ToHashSet();
            double Score(SgdbGame g)
            {
                var nameTokens = Tokenize(g.Name);
                int hit = nameTokens.Count(tokens.Contains);
                double s = tokens.Count == 0 ? 0 : (double)hit / tokens.Count;
                if (g.Verified) s += 0.05;
                if (hit == tokens.Count && tokens.Count > 0) s += 0.5; // 搜索词全部命中
                return s;
            }
            list = list.OrderByDescending(Score).ToList();
            return (list, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, $"SGDB 请求失败: {ex.Message}"); }
    }

    /// <summary>搜索词归一化：字母/数字边界插空格（"warfare4"→"warfare 4"，否则 SGDB 分词匹配不上）、压缩空白。</summary>
    internal static string NormalizeSearchTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return "";
        var t = Regex.Replace(term.Trim(), "(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])", " ");
        return Regex.Replace(t, "\\s+", " ");
    }

    private static string[] Tokenize(string s) =>
        Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0).ToArray();

    /// <summary>按 Steam AppID 精确取候选封面（绕过名称搜索——系列游戏名称搜索常被挤出结果）。</summary>
    public async Task<(List<SgdbGrid>? Data, string? Error)> GetCandidatesBySteamAppIdAsync(string appId, CancellationToken ct = default)
    {
        var key = GetSgdbKey();
        if (key == null) return (null, "未配置 SteamGridDB API Key");
        return await GridsListAsync($"grids/steam/{appId}", key, ct);
    }

    /// <summary>
    /// 从用户输入提取 Steam AppID：支持纯数字，也支持商店链接
    /// （https://store.steampowered.com/app/4435490/...）。解析失败返回 null。
    /// </summary>
    internal static string? ExtractSteamAppId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        var idx = s.IndexOf("/app/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rest = s[(idx + 5)..];
            var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            return digits.Length >= 2 && digits.Length <= 10 ? digits : null;
        }
        return long.TryParse(s, out var n) && n >= 10 && s.Length <= 10 ? s : null;
    }

    /// <summary>名称搜索 → 游戏 ID。策略: verified 且名称去空格全等 → 第一个 verified → 第一个。</summary>
    private async Task<(string? GameId, string? Error)> SearchGameIdAsync(string name, string key, CancellationToken ct)
    {
        var (list, err) = await SearchCoreAsync(name, key, ct);
        if (err != null) return (null, err);
        if (list == null || list.Count == 0) return (null, "SGDB 未找到匹配游戏");

        static string Norm(string s) => s.Replace(" ", "").ToLowerInvariant();
        var target = Norm(name);
        var exact = list.FirstOrDefault(g => g.Verified && Norm(g.Name) == target);
        if (exact != null) return (exact.Id.ToString(), null);
        var firstVerified = list.FirstOrDefault(g => g.Verified);
        if (firstVerified != null) return (firstVerified.Id.ToString(), null);
        return (list[0].Id.ToString(), null);
    }

    /// <summary>编辑弹窗「候选图列表」代理。</summary>
    public async Task<(List<SgdbGrid>? Data, string? Error)> GetCandidatesAsync(string gameId, CancellationToken ct = default)
    {
        var key = GetSgdbKey();
        if (key == null) return (null, "未配置 SteamGridDB API Key");
        return await GridsListAsync($"grids/game/{gameId}", key, ct);
    }

    // ── 图片下载与文件管理 ───────────────────────────────────

    private async Task<string?> DownloadToFileAsync(string url, string profileId, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return $"HTTP {(int)res.StatusCode}";

            var media = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!media.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return $"非图片响应 ({media})";

            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return "空响应";
            if (bytes.Length > MaxImageBytes) return "图片超过 20MB";

            WriteCover(profileId, DetectExt(media, url), bytes);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>写入/替换封面（tmp + 原子 Move + 清理其它扩展名旧文件）。返回错误或 null。</summary>
    private string? WriteCover(string profileId, string ext, byte[] bytes)
    {
        try
        {
            var target = Path.Combine(_coversDir, profileId + ext);
            var tmp = target + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, overwrite: true);

            foreach (var f in Directory.GetFiles(_coversDir, profileId + ".*"))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
                    File.Delete(f);
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string DetectExt(string media, string url) => media switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    // ── 公开文件操作 ─────────────────────────────────────────

    public string? FindCoverFile(string profileId)
    {
        try
        {
            return Directory.GetFiles(_coversDir, profileId + ".*")
                .FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public bool HasCover(string profileId) => FindCoverFile(profileId) != null;

    /// <summary>封面版本号（LastWriteTimeUtc.Ticks），前端用作缓存破坏参数。</summary>
    public string? GetCoverVer(string profileId)
    {
        var f = FindCoverFile(profileId);
        if (f == null) return null;
        try { return File.GetLastWriteTimeUtc(f).Ticks.ToString(); }
        catch { return "0"; }
    }

    public void DeleteCover(string profileId)
    {
        try
        {
            foreach (var f in Directory.GetFiles(_coversDir, profileId + ".*"))
                File.Delete(f);
        }
        catch (Exception ex) { AppLog.Write("GameCover", $"DeleteCover({profileId}): {ex.Message}"); }
    }

    /// <summary>上传本地图片（base64 dataUrl，≤8MB）。返回错误或 null。</summary>
    public string? UploadFromBase64(string profileId, string dataUrl)
    {
        var idx = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
        if (idx <= 0 || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return "仅支持图片 base64 dataUrl";
        var media = dataUrl["data:".Length..idx];
        try
        {
            var bytes = Convert.FromBase64String(dataUrl[(idx + 8)..]);
            if (bytes.Length == 0) return "空数据";
            if (bytes.Length > MaxUploadBytes) return "图片不能超过 8MB";
            return WriteCover(profileId, DetectExt(media, ""), bytes);
        }
        catch (FormatException) { return "base64 解码失败"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>下载用户在编辑弹窗选定的候选图。仅允许 https + *.steamgriddb.com（防 SSRF）。</summary>
    public async Task<string?> DownloadFromUrlAsync(string profileId, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("steamgriddb.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".steamgriddb.com", StringComparison.OrdinalIgnoreCase)))
            return "仅允许 SteamGridDB 图片地址";
        return await DownloadToFileAsync(url, profileId, CancellationToken.None);
    }

    /// <summary>启动清理: covers/ 下无对应 profile 的文件（含残留 .tmp）。</summary>
    public void CleanupOrphans()
    {
        try
        {
            var ids = _profiles.GetAll().Select(p => p.Id).ToHashSet();
            foreach (var f in Directory.GetFiles(_coversDir))
            {
                try
                {
                    if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(f);
                        continue;
                    }
                    if (!ids.Contains(Path.GetFileNameWithoutExtension(f)))
                    {
                        File.Delete(f);
                        AppLog.Write("GameCover", $"清理无主封面 {Path.GetFileName(f)}");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { AppLog.Write("GameCover", $"CleanupOrphans: {ex.Message}"); }
    }

    // ── 选项（SGDB Key）─────────────────────────────────────

    public bool HasKey => GetSgdbKey() != null;

    /// <summary>读取 SGDB Key；未配置返回 null。绝不出现在任何 API 响应中。</summary>
    public string? GetSgdbKey()
    {
        lock (_optsLock)
        {
            var k = LoadOpts().SgdbKey;
            return string.IsNullOrWhiteSpace(k) ? null : k;
        }
    }

    // cover-opts.json 用大小写不敏感解析：外部工具（PowerShell ConvertTo-Json 等）
    // 写出的是 camelCase "sgdbKey"，默认 STJ 区分大小写会静默匹配失败
    private static readonly JsonSerializerOptions OptsJson = new() { PropertyNameCaseInsensitive = true };

    public void SetSgdbKey(string key)
    {
        lock (_optsLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(new CoverOpts { SgdbKey = key.Trim() }, OptsJson);
                var tmp = _optsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _optsPath, overwrite: true);
                AppLog.Write("GameCover", $"SGDB Key 已{(string.IsNullOrWhiteSpace(key) ? "清除" : "保存")}");
            }
            catch (Exception ex) { AppLog.Write("GameCover", $"保存 cover-opts.json 失败: {ex.Message}"); }
        }
    }

    private CoverOpts LoadOpts()
    {
        try
        {
            if (File.Exists(_optsPath))
                return JsonSerializer.Deserialize<CoverOpts>(File.ReadAllText(_optsPath), OptsJson) ?? new CoverOpts();
        }
        catch (Exception ex)
        {
            AppLog.Write("GameCover", $"cover-opts.json 解析失败: {ex.Message}");
        }
        return new CoverOpts();
    }

    // ── 队列与进度 ───────────────────────────────────────────

    /// <summary>批量入队。非 force 时跳过已有封面；已入队的跳过（防重复）。返回实际入队数。</summary>
    public int Enqueue(IEnumerable<string> profileIds, bool force)
    {
        // 上一批是否已完结——判断+清零都必须在写入本轮任务【之前】执行：
        // 双消费者下本轮任务入队后可能瞬间完成，若清零发生在入队之后，
        // 会把已完成任务的计数抹掉 → done+failed 永远追不上 total → 前端卡死
        //（清零时 running=0 且上一批已全部入账，此时还没有任何本轮任务，清零零风险）
        var canReset = Volatile.Read(ref _running) == 0 &&
                       Volatile.Read(ref _done) + Volatile.Read(ref _failed) >= Volatile.Read(ref _total);
        if (canReset)
        {
            Interlocked.Exchange(ref _total, 0);
            Interlocked.Exchange(ref _done, 0);
            Interlocked.Exchange(ref _failed, 0);
        }

        int n = 0;
        foreach (var id in profileIds)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (!force && HasCover(id)) continue;
            if (!_queued.TryAdd(id, 0)) continue;
            _queue.Writer.TryWrite(new CoverJob(id, force));
            n++;
        }

        if (n > 0)
        {
            Interlocked.Add(ref _total, n);
        }
        return n;
    }

    public CoverProgress GetProgress()
    {
        var failures = _failures
            .Select(kv =>
            {
                var p = _profiles.GetById(kv.Key);
                return new CoverFailure(kv.Key, p?.Name ?? kv.Key, kv.Value);
            })
            .ToList();
        return new CoverProgress(
            Volatile.Read(ref _total),
            Volatile.Read(ref _done),
            Volatile.Read(ref _failed),
            Volatile.Read(ref _running),
            failures);
    }
}
