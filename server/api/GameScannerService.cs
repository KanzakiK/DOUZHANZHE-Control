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
                        Source = "steam"
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
                    Source = "epic"
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
}
