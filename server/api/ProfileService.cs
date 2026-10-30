// SPDX-License-Identifier: GPL-3.0-only
//
// ProfileService - Configuration (Preset) Management Service
// Responsibilities:
//   - CRUD: Manage config/profiles/ directory
//   - .index.json manifest maintenance
//   - Built-in profile protection (cannot delete/rename)
//   - First-run migration from overrides-{mode}.json

using System.Text.Json;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public sealed class ProfileService
{
    private readonly string _profilesDir;
    private readonly string _indexPath;
    private readonly object _lock = new();
    private ProfileIndex _index = new();  // 非空保证：LoadIndex 只会赋非空实例
    private bool _loaded;                 // 惰性加载标志（_index 恒非空，不能再用 null 判断）

    private static readonly (string Id, string Name, string ThermalMode)[] BuiltInProfiles =
    [
        ("cfg-silent", "\u5b89\u9759", "silent"),
        ("cfg-office", "\u5747\u8861", "office"),
        ("cfg-gaming", "\u6597\u6218", "gaming"),
        ("cfg-beast",  "\u91ce\u517d", "beast"),
    ];

    /// <summary>
    /// 配置 id 前缀。统一用连字符，Windows 文件名、last-mode.json、前端 settings.mode、
    /// 游戏 TargetMode、磁盘文件名全部同一串表示，与 EC 性能模式裸名(silent/office/beast/gaming)
    /// 彻底区分，避免"一值两义"，且无需任何额外编码（KISS）。
    /// </summary>
    public const string Prefix = "cfg-";

    private static bool IsBuiltinId(string id)
        => BuiltInProfiles.Any(bp => bp.Id == id);

    public ProfileService(string configDir)
    {
        _profilesDir = Path.Combine(configDir, "profiles");
        _indexPath = Path.Combine(_profilesDir, ".index.json");
    }

    public void EnsureInitialized(string configDir)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_profilesDir))
            {
                Directory.CreateDirectory(_profilesDir);
                _index = new ProfileIndex();
                _loaded = true; // 目录刚创建，索引必然为空，无需读盘
            }

            LoadIndex();

            foreach (var bp in BuiltInProfiles)
            {
                if (!_index.Profiles.Any(p => p.Id == bp.Id))
                {
                    var srcFile = Path.Combine(configDir, $"overrides-{bp.Id}.json");
                    var dstFile = ProfilePath(bp.Id);
                    if (File.Exists(srcFile) && !File.Exists(dstFile))
                        File.Copy(srcFile, dstFile);
                    else if (!File.Exists(dstFile))
                        WriteProfileJson(bp.Id, new PerformanceOverrides());

                    _index.Profiles.Add(new ProfileEntry
                    {
                        Id = bp.Id,
                        Name = bp.Name,
                        BuiltIn = true,
                        ThermalMode = bp.ThermalMode,
                    });
                }
            }

            var seen = new HashSet<string>();
            var deduped = new List<ProfileEntry>();
            foreach (var p in _index.Profiles)
            {
                if (seen.Add(p.Id)) deduped.Add(p);
            }
            _index.Profiles = deduped;

            SaveIndex();
        }
    }

    public List<ProfileEntry> GetAll()
    {
        lock (_lock)
        {
            LoadIndex();
            return [.. _index.Profiles];
        }
    }

    public (ProfileEntry Entry, PerformanceOverrides Overrides)? GetById(string id)
    {
        lock (_lock)
        {
            LoadIndex();
            var entry = _index.Profiles.FirstOrDefault(p => p.Id == id);
            if (entry == null) return null;
            var overrides = ReadProfileJson(id) ?? new PerformanceOverrides();
            return (entry, overrides);
        }
    }

    public bool SaveOverrides(string id, PerformanceOverrides overrides)
    {
        lock (_lock)
        {
            LoadIndex();
            if (!_index.Profiles.Any(p => p.Id == id)) return false;
            WriteProfileJson(id, overrides);
            return true;
        }
    }

    public ProfileEntry? Create(string name, string? thermalMode = null)
    {
        lock (_lock)
        {
            LoadIndex();
            var id = SanitizeId(name);
            if (string.IsNullOrEmpty(id)) return null;
            if (_index.Profiles.Any(p => p.Id == id))
            {
                var baseId = id;
                int n = 2;
                while (_index.Profiles.Any(p => p.Id == $"{baseId}-{n}"))
                    n++;
                id = $"{baseId}-{n}";
            }

            var entry = new ProfileEntry
            {
                Id = id,
                Name = name,
                BuiltIn = false,
                ThermalMode = thermalMode ?? "office",
                CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            };

            _index.Profiles.Add(entry);
            SaveIndex();
            WriteProfileJson(id, new PerformanceOverrides());
            return entry;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            LoadIndex();
            var entry = _index.Profiles.FirstOrDefault(p => p.Id == id);
            if (entry == null || entry.BuiltIn) return false;

            _index.Profiles.Remove(entry);
            SaveIndex();

            var path = ProfilePath(id);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
    }

    public bool Rename(string id, string newName)
    {
        lock (_lock)
        {
            LoadIndex();
            var entry = _index.Profiles.FirstOrDefault(p => p.Id == id);
            if (entry == null || entry.BuiltIn) return false;
            entry.Name = newName;
            SaveIndex();
            return true;
        }
    }

    public ProfileEntry? Copy(string id)
    {
        lock (_lock)
        {
            LoadIndex();
            var src = _index.Profiles.FirstOrDefault(p => p.Id == id);
            if (src == null) return null;

            var overrides = ReadProfileJson(id) ?? new PerformanceOverrides();
            var newName = src.Name + " (\u526f\u672c)";
            var newId = SanitizeId(newName);
            if (_index.Profiles.Any(p => p.Id == newId))
            {
                var baseId = newId;
                int n = 2;
                while (_index.Profiles.Any(p => p.Id == $"{baseId}-{n}"))
                    n++;
                newId = $"{baseId}-{n}";
            }

            var entry = new ProfileEntry
            {
                Id = newId,
                Name = newName,
                BuiltIn = false,
                ThermalMode = src.ThermalMode,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            };

            _index.Profiles.Add(entry);
            SaveIndex();
            WriteProfileJson(newId, overrides);
            return entry;
        }
    }

    public bool ResetToDefaults(string id)
    {
        lock (_lock)
        {
            LoadIndex();
            if (!_index.Profiles.Any(p => p.Id == id)) return false;
            WriteProfileJson(id, new PerformanceOverrides());
            return true;
        }
    }

    public bool SetThermalMode(string id, string thermalMode)
    {
        lock (_lock)
        {
            LoadIndex();
            var entry = _index.Profiles.FirstOrDefault(p => p.Id == id);
            if (entry == null || entry.BuiltIn) return false;
            entry.ThermalMode = thermalMode;
            SaveIndex();
            return true;
        }
    }

    // ---- Internals ----

    private void LoadIndex()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(_indexPath))
            {
                var json = File.ReadAllText(_indexPath);
                _index = JsonSerializer.Deserialize<ProfileIndex>(json, JsonOpts) ?? new ProfileIndex();
            }
        }
        catch { _index = new ProfileIndex(); }
    }

    private void SaveIndex()
    {
        var json = JsonSerializer.Serialize(_index, JsonOpts);
        var tmp = _indexPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _indexPath, overwrite: true);
    }

    private string ProfilePath(string id) => Path.Combine(_profilesDir, $"{id}.json");

    private PerformanceOverrides? ReadProfileJson(string id)
    {
        var path = ProfilePath(id);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PerformanceOverrides>(json, JsonOpts) ?? new PerformanceOverrides();
        }
        catch { return null; }
    }

    private void WriteProfileJson(string id, PerformanceOverrides overrides)
    {
        var path = ProfilePath(id);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(overrides, JsonOpts);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static string SanitizeId(string name)
    {
        var chars = name.Select(c =>
        {
            if (char.IsLetterOrDigit(c)) return char.ToLowerInvariant(c);
            if (c == ' ') return '-';
            return '\0';
        }).Where(c => c != '\0').ToArray();
        var id = new string(chars).Trim('-');
        // 用户配置 id 统一加 cfg- 前缀，与 EC 性能模式裸名彻底区分。
        if (id.Length == 0) return Prefix + "profile";
        return Prefix + id;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    public class ProfileIndex
    {
        public List<ProfileEntry> Profiles { get; set; } = [];
    }

    public class ProfileEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool BuiltIn { get; set; }
        public string ThermalMode { get; set; } = "office";
        public string? CreatedAt { get; set; }
    }
}
