// SPDX-License-Identifier: GPL-3.0-only
//
// GameScannerService — Steam / Epic 游戏自动扫描
// ================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public class GameScanResult
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Source { get; set; } = ""; // steam, epic
    public bool AlreadyAdded { get; set; }
    // steam=Steam AppID（来自 appmanifest_{appid}.acf 文件名）/ epic=CatalogItemId（.item 清单）
    // 供封面服务做 SteamGridDB 精确匹配与官方 CDN 直链兜底
    public string AppId { get; set; } = "";
}

public static class GameScannerService
{
    // 非游戏程序关键词（不区分大小写）
    private static readonly string[] UtilityKeywords =
    {
        "unins", "setup", "install", "crash", "report", "diagnostic",
        "benchmark", "vanguard", "wallpaper", "lossless", "overlay",
        "sdk", "editor", "server", "launcher", "updater", "patcher",
        "3dmark", "vrmark", "compatibility"
    };

    public static List<GameScanResult> Scan(GameProfileService profiles)
    {
        var results = new List<GameScanResult>();
        var existingNames = new HashSet<string>(
            profiles.GetAll().Select(p => p.ExeName.ToLowerInvariant()));

        ScanSteam(results, existingNames);
        ScanEpic(results, existingNames);

        return results;
    }

    // ── Steam 扫描 ───────────────────────────────────────────

    private static void ScanSteam(List<GameScanResult> results, HashSet<string> existing)
    {
        var vdfPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
        if (!File.Exists(vdfPath)) return;

        var libraryPaths = new List<string>();
        try
        {
            var vdf = File.ReadAllText(vdfPath);
            foreach (Match m in Regex.Matches(vdf, @"""path""\s+""([^""]+)"""))
                libraryPaths.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch { return; }

        foreach (var lib in libraryPaths)
        {
            var appsDir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (var acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var content = File.ReadAllText(acf);
                    var name = ExtractVdfValue(content, "name");
                    var installDir = ExtractVdfValue(content, "installdir");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir)) continue;

                    var fullDir = Path.Combine(appsDir, "common", installDir);
                    if (!Directory.Exists(fullDir)) continue;

                    if (IsUtility(name)) continue;

                    var exePath = FindMainExe(fullDir, name);
                    if (string.IsNullOrEmpty(exePath)) continue;

                    var exeName = Path.GetFileName(exePath).ToLowerInvariant();
                    if (existing.Contains(exeName) || results.Any(r =>
                        r.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    results.Add(new GameScanResult
                    {
                        Name = name,
                        ExePath = exePath,
                        Source = "steam",
                        AppId = Path.GetFileNameWithoutExtension(acf).Split('_').Last()
                    });
                }
                catch { }
            }
        }

        AppLog.Write("GameScan", $"Steam: found {results.Count(r => r.Source == "steam")} games");
    }

    private static string? ExtractVdfValue(string content, string key)
    {
        var m = Regex.Match(content, $@"""{key}""\s+""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── Epic 扫描 ────────────────────────────────────────────

    private static void ScanEpic(List<GameScanResult> results, HashSet<string> existing)
    {
        var manifestDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(manifestDir)) return;

        int count = 0;
        foreach (var itemFile in Directory.GetFiles(manifestDir, "*.item"))
        {
            try
            {
                var json = File.ReadAllText(itemFile);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var displayName = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                var installLoc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                var launchExe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                var catalogId = root.TryGetProperty("CatalogItemId", out var ci) ? ci.GetString() : null;

                if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(installLoc) ||
                    string.IsNullOrEmpty(launchExe))
                    continue;
                if (!Directory.Exists(installLoc)) continue;
                if (IsUtility(displayName)) continue;

                var exePath = Path.Combine(installLoc, launchExe);
                var exeName = Path.GetFileName(exePath).ToLowerInvariant();

                if (existing.Contains(exeName) || results.Any(r =>
                    r.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                results.Add(new GameScanResult
                {
                    Name = displayName,
                    ExePath = exePath,
                    Source = "epic",
                    AppId = catalogId ?? ""
                });
                count++;
            }
            catch { }
        }

        AppLog.Write("GameScan", $"Epic: found {count} games");
    }

    // ── 通用工具 ─────────────────────────────────────────────

    private static bool IsUtility(string name)
    {
        var lower = name.ToLowerInvariant();
        return UtilityKeywords.Any(kw => lower.Contains(kw));
    }

    /// <summary>
    /// 在游戏安装目录中定位主 exe。
    /// 策略：匹配目录名 → 根目录最大 exe → Binaries/Win64 (UE) → 子目录最大 exe
    /// </summary>
    private static string? FindMainExe(string installDir, string gameName)
    {
        // 1. 根目录：名称匹配优先
        var rootExes = SafeGetExes(installDir);
        var nameMatch = rootExes.FirstOrDefault(e =>
            Path.GetFileNameWithoutExtension(e)
                .Replace(" ", "").Equals(gameName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (nameMatch != null) return nameMatch;

        // 2. 根目录：最大 exe（排除工具类）
        var best = FilterAndPickLargest(rootExes);
        if (best != null) return best;

        // 3. UE 模式: Binaries/Win64/
        var ueDir = Path.Combine(installDir, "Binaries", "Win64");
        if (Directory.Exists(ueDir))
        {
            var ueExes = SafeGetExes(ueDir);
            var ueBest = FilterAndPickLargest(ueExes);
            if (ueBest != null) return ueBest;
        }

        // 4. 一层子目录搜索
        try
        {
            foreach (var sub in Directory.GetDirectories(installDir))
            {
                if (Path.GetFileName(sub).StartsWith(".")) continue;
                var subExes = SafeGetExes(sub);
                var subBest = FilterAndPickLargest(subExes);
                if (subBest != null) return subBest;
            }
        }
        catch { }

        return null;
    }

    private static string[] SafeGetExes(string dir)
    {
        try { return Directory.GetFiles(dir, "*.exe"); }
        catch { return Array.Empty<string>(); }
    }

    private static string? FilterAndPickLargest(string[] exes)
    {
        var candidates = exes.Where(e => !IsUtility(Path.GetFileName(e))).ToArray();
        if (candidates.Length == 0) return null;
        return candidates.OrderByDescending(e =>
        {
            try { return new FileInfo(e).Length; } catch { return 0L; }
        }).First();
    }

    // ── 封面服务支撑：商店 ID 映射与存量回填 ─────────────────

    /// <summary>
    /// 扫描 Steam/Epic 清单，构建「安装目录 → 商店 ID」映射。
    /// steam: appmanifest_{appid}.acf 的 installdir；epic: .item 的 InstallLocation → CatalogItemId。
    /// 供封面服务对存量 profile（无 StoreAppId）按 ExePath 前缀反查回填。
    /// </summary>
    public static (Dictionary<string, string> Steam, Dictionary<string, string> Epic) BuildStoreIdMaps()
    {
        var steam = new Dictionary<string, string>(StringComparer.Ordinal);
        var epic = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var vdfPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
            if (File.Exists(vdfPath))
            {
                var libraryPaths = new List<string>();
                foreach (Match m in Regex.Matches(File.ReadAllText(vdfPath), @"""path""\s+""([^""]+)"""))
                    libraryPaths.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

                foreach (var lib in libraryPaths)
                {
                    var appsDir = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(appsDir)) continue;

                    foreach (var acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
                    {
                        try
                        {
                            var appId = Path.GetFileNameWithoutExtension(acf).Split('_').Last();
                            var installDir = ExtractVdfValue(File.ReadAllText(acf), "installdir");
                            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(installDir)) continue;
                            steam[NormDirKey(Path.Combine(appsDir, "common", installDir))] = appId;
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        try
        {
            var manifestDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            if (Directory.Exists(manifestDir))
            {
                foreach (var itemFile in Directory.GetFiles(manifestDir, "*.item"))
                {
                    try
                    {
                        var root = JsonDocument.Parse(File.ReadAllText(itemFile)).RootElement;
                        var id = root.TryGetProperty("CatalogItemId", out var ci) ? ci.GetString() : null;
                        var loc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(loc)) continue;
                        epic[NormDirKey(loc)] = id;
                    }
                    catch { }
                }
            }
        }
        catch { }

        return (steam, epic);
    }

    /// <summary>
    /// 存量回填：对 steam/epic 来源且 StoreAppId 为空的 profile，
    /// 用安装目录映射按 ExePath 前缀匹配反查商店 ID 并写回（锁内、幂等）。
    /// </summary>
    public static void BackfillStoreAppIds(GameProfileService profiles)
    {
        var targets = profiles.GetAll()
            .Where(p => string.IsNullOrEmpty(p.StoreAppId) && (p.Source == "steam" || p.Source == "epic"))
            .ToList();
        if (targets.Count == 0) return;

        var (steam, epic) = BuildStoreIdMaps();
        int n = 0;
        foreach (var p in targets)
        {
            var exeNorm = NormExePath(p.ExePath);
            if (exeNorm == null) continue;
            var map = p.Source == "steam" ? steam : epic;
            foreach (var kv in map)
            {
                if (exeNorm.StartsWith(kv.Key, StringComparison.Ordinal))
                {
                    if (profiles.UpdateCoverMeta(p.Id, storeAppId: kv.Value)) n++;
                    break;
                }
            }
        }
        if (n > 0)
            AppLog.Write("GameScan", $"存量 StoreAppId 回填 {n}/{targets.Count} 条");
    }

    /// <summary>目录归一化：全路径大写 + 统一尾部分隔符（作前缀匹配键）。</summary>
    private static string NormDirKey(string p)
    {
        try
        {
            return Path.GetFullPath(p)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant() + "\\";
        }
        catch { return ""; }
    }

    private static string? NormExePath(string p)
    {
        try { return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(p).ToUpperInvariant(); }
        catch { return null; }
    }
}
