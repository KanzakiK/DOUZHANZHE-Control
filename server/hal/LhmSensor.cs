// SPDX-License-Identifier: MIT
//
// LhmSensor — LibreHardwareMonitor 薄封装
// ==========================================
// 职责：
//   通过 LHM 读取 AMD CPU die temperature（SMN 总线），完全绕过 EC。
//   1 秒限频缓存 + lock 线程安全 + Math.Round 转 byte。
//   WinRing0 驱动由 ryzenadj 启动时加载，LHM 直接复用。

using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Douzhanzhe.HAL;

public static class LhmSensor
{
    private static Computer? _pc;
    private static readonly object _lock = new();
    private static byte _cachedTemp;
    private static DateTime _lastUpdate = DateTime.MinValue;
    private static volatile bool _opened;

    /// <summary>
    /// 初始化 LHM Computer 对象。
    /// 在 Program.cs 启动流程中 WinRing0 加载之后调用。
    /// </summary>
    public static void Open()
    {
        lock (_lock)
        {
            try
            {
                _pc?.Close();
                _pc = new Computer { IsCpuEnabled = true };
                _pc.Open();
                _opened = true;
                _lastUpdate = DateTime.MinValue; // 重置缓存，强制下次 Update
                AppLog.Write("LHM", "Computer.Open() 成功");
            }
            catch (Exception ex)
            {
                _opened = false;
                AppLog.Write("LHM", $"Computer.Open() 失败: {ex.Message}");
            }
        }
    }

    /// <summary>关闭 LHM Computer 对象（睡眠恢复前调用）</summary>
    public static void Close()
    {
        lock (_lock)
        {
            try { _pc?.Close(); } catch { }
            _pc = null;
            _opened = false;
            AppLog.Write("LHM", "Computer.Close()");
        }
    }

    /// <summary>
    /// 读取 CPU 温度 (°C)。线程安全，1 秒限频缓存。
    /// 返回 0 表示 LHM 不可用或读取失败。
    /// </summary>
    public static byte GetCpuTemperature()
    {
        lock (_lock)
        {
            if (!_opened || _pc == null) return _cachedTemp;

            // 1 秒限频：缓存期内直接返回
            if ((DateTime.UtcNow - _lastUpdate).TotalSeconds < 1)
                return _cachedTemp;

            try
            {
                foreach (var hw in _pc.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        hw.Update();
                        // 查找 Core 温度传感器（AMD 的 die temperature）
                        var sensor = hw.Sensors.FirstOrDefault(
                            s => s.SensorType == SensorType.Temperature
                              && s.Name.Contains("Core"));
                        if (sensor?.Value != null)
                        {
                            var val = (byte)Math.Round(sensor.Value.Value);
                            if (val > 0) _cachedTemp = val;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("LHM", $"Update 异常: {ex.Message}");
            }

            _lastUpdate = DateTime.UtcNow;
            return _cachedTemp;
        }
    }

    /// <summary>LHM 是否已成功初始化</summary>
    public static bool IsOpen => _opened;
}
