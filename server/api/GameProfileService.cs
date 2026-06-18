// SPDX-License-Identifier: GPL-3.0-only
//
// GameProfileService — 游戏配置管理
// ====================================
// 职责：
//   - 游戏规则 CRUD
//   - JSON 持久化
//   - 全局配置管理

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public sealed class GameProfileService
{
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "config", "game-profiles.json");

    private readonly object _lock = new();
    private GameProfilesData _data = new();

    public GameProfileService()
    {
        Load();
    }

    // ── 全局配置 ──────────────────────────────────────────

    public bool Enabled
    {
        get { lock (_lock) return _data.Enabled; }
        set { lock (_lock) { _data.Enabled = value; Save(); } }
    }

    public string DefaultMode
    {
        get { lock (_lock) return _data.DefaultMode; }
        set { lock (_lock) { _data.DefaultMode = value; Save(); } }
    }

    // ── 规则查询 ──────────────────────────────────────────

    public List<GameProfile> GetAll()
    {
        lock (_lock) return _data.Profiles.ToList();
    }

    public GameProfile? GetById(string id)
    {
        lock (_lock) return _data.Profiles.FirstOrDefault(p => p.Id == id);
    }

    public GameProfile? MatchByExeName(string exeName)
    {
        lock (_lock)
        {
            return _data.Profiles.FirstOrDefault(p =>
                p.Enabled &&
                string.Equals(p.ExeName, exeName, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── 规则 CRUD ─────────────────────────────────────────

    public GameProfile Add(GameProfile profile)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N")[..8];

            // 从路径自动提取进程名（前端可能不传）
            if (string.IsNullOrEmpty(profile.ExeName) && !string.IsNullOrEmpty(profile.ExePath))
                profile.ExeName = Path.GetFileName(profile.ExePath);

            // 去重检查
            var existing = _data.Profiles.FirstOrDefault(p =>
                string.Equals(p.ExeName, profile.ExeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                throw new InvalidOperationException($"规则已存在: {profile.ExeName}");

            _data.Profiles.Add(profile);
            Save();
            return profile;
        }
    }

    public GameProfile Update(string id, GameProfile updated)
    {
        lock (_lock)
        {
            var profile = _data.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null)
                throw new KeyNotFoundException($"规则不存在: {id}");

            // 从路径自动提取进程名（前端可能不传）
            if (string.IsNullOrEmpty(updated.ExeName) && !string.IsNullOrEmpty(updated.ExePath))
                updated.ExeName = Path.GetFileName(updated.ExePath);

            // 检查 exeName 重复（排除自身）
            var duplicate = _data.Profiles.FirstOrDefault(p =>
                p.Id != id &&
                string.Equals(p.ExeName, updated.ExeName, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
                throw new InvalidOperationException($"规则已存在: {updated.ExeName}");

            profile.Name = updated.Name;
            profile.ExePath = updated.ExePath;
            profile.ExeName = updated.ExeName;
            profile.TargetMode = updated.TargetMode;
            profile.Enabled = updated.Enabled;
            profile.Source = updated.Source;

            Save();
            return profile;
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            var profile = _data.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile != null)
            {
                _data.Profiles.Remove(profile);
                Save();
            }
        }
    }

    public void UpdateConfig(bool? enabled, string? defaultMode)
    {
        lock (_lock)
        {
            if (enabled.HasValue) _data.Enabled = enabled.Value;
            if (!string.IsNullOrEmpty(defaultMode)) _data.DefaultMode = defaultMode;
            Save();
        }
    }

    // ── 持久化 ─────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                _data = JsonSerializer.Deserialize<GameProfilesData>(json) ?? new GameProfilesData();
                AppLog.Write("GameProfile", $"Loaded {_data.Profiles.Count} profiles");
            }
            else
            {
                _data = new GameProfilesData();
                Save();
                AppLog.Write("GameProfile", "Created new config file");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("GameProfile", $"Load failed: {ex.Message}");
            _data = new GameProfilesData();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            AppLog.Write("GameProfile", $"Save failed: {ex.Message}");
        }
    }
}

// ── 数据模型 ─────────────────────────────────────────────────

public class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string TargetMode { get; set; } = "gaming";
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "manual"; // manual, steam, epic
}

public class GameProfilesData
{
    public bool Enabled { get; set; } = true;
    public string DefaultMode { get; set; } = "gaming";
    public List<GameProfile> Profiles { get; set; } = new();
}

// ── 请求模型 ─────────────────────────────────────────────────

public record GameProfileRequest(
    string? Name,
    string? ExePath,
    string? ExeName,
    string? TargetMode,
    bool? Enabled,
    string? Source);

public record GameConfigRequest(bool? Enabled, string? DefaultMode);
