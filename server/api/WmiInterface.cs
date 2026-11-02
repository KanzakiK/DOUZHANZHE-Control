// SPDX-License-Identifier: MIT
//
// WmiInterface -- WMI ACPI MICommonInterface ??
// ==============================================
// ?? root\WMI ?????? MiInterface ????????
// ??? DLL ?????? System.Management ???
//
// ???? (32 ??):
//   InData[1] = 250(Get) / 251(Set)
//   InData[3] = ????
//   InData[4] = ??? (Set ?)
//   OutData[4..11] = ???

using System.Management;

namespace Douzhanzhe.API;

public sealed class WmiInterface
{
    private readonly ManagementScope _scope = null!;
    private readonly bool _available;
    private readonly string _error;

    // ---- 读缓存 ----
    // 这些 MICommonInterface 方法调用单次往返需数百毫秒（WMI 忙时可到数秒），
    // 而遥测帧每 250ms 就要读 4 个 → 不缓存会把遥测循环拖到数十秒一轮，
    // 前端因此长时间停在"暂无实时数据"。改为：读走缓存 + 后台刷新。
    private readonly object _cacheLock = new();
    private volatile bool _refreshing;
    private byte _cachedThermalMode;
    private byte _cachedGpuMode;
    private byte _cachedFnLock;
    private byte _cachedTouchpadLock;
    private DateTime _thermalModeTime = DateTime.MinValue;
    private DateTime _gpuModeTime = DateTime.MinValue;
    private DateTime _fnLockTime = DateTime.MinValue;
    private DateTime _touchpadLockTime = DateTime.MinValue;
    private const int CacheTtlSeconds = 2;

    public WmiInterface()
    {
        try
        {
            _scope = new ManagementScope(@"root\WMI");
            _scope.Connect();

            using var obj = new ManagementObject(
                _scope,
                new ManagementPath(@"MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'"),
                null);
            obj.Get();
            _available = true;
            _error = string.Empty;

            // 启动时后台预热缓存（不等它完成，避免拖慢端口监听）；
            // 需要实时值的启动流程请用 GetGpuModeFresh()
            _ = System.Threading.Tasks.Task.Run(() => { try { RefreshCacheCore(); } catch { } });
        }
        catch (Exception ex)
        {
            _available = false;
            _error = ex.GetType().Name + ":" + ex.Message;
        }
    }

    public bool Available => _available;
    public string Error => _error;
    public string Status => _available ? "ready" : "error";

    private byte[] CallMethod(byte[] input)
    {
        using var obj = new ManagementObject(
            "root\\WMI",
            "MICommonInterface.InstanceName='ACPI\\PNP0C14\\MIFS_0'",
            null);
        using var inParams = obj.GetMethodParameters("MiInterface");
        inParams["InData"] = input;
        using var outParams = obj.InvokeMethod("MiInterface", inParams, null);
        return (byte[])outParams["OutData"];
    }

    // ---- SystemPerMode (?? 8) — 散热模式切换 ----
    public byte GetThermalMode()
    {
        EnsureCacheFresh();
        lock (_cacheLock) return _cachedThermalMode;
    }

    /// <summary>缓存失效时触发后台刷新；刷新期间读取直接返回旧值，不阻塞调用方。</summary>
    private void EnsureCacheFresh()
    {
        var now = DateTime.UtcNow;
        bool stale = (now - _thermalModeTime).TotalSeconds >= CacheTtlSeconds
                  || (now - _gpuModeTime).TotalSeconds >= CacheTtlSeconds
                  || (now - _fnLockTime).TotalSeconds >= CacheTtlSeconds
                  || (now - _touchpadLockTime).TotalSeconds >= CacheTtlSeconds;
        if (!stale) return;

        lock (_cacheLock)
        {
            if (_refreshing) return;
            _refreshing = true;
        }
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { RefreshCacheCore(); }
            catch { /* 单项失败不影响其它项 */ }
            finally { lock (_cacheLock) _refreshing = false; }
        });
    }

    /// <summary>实际执行 4 项 WMI 读取并写入缓存（耗时较长，调用方应放到后台）。</summary>
    private void RefreshCacheCore()
    {
        byte thermal = 0, gpu = 0, fn = 0, tp = 0;
        bool gotThermal = false, gotGpu = false, gotFn = false, gotTp = false;
        try { thermal = ReadThermalMode(); gotThermal = true; } catch { }
        try { gpu = ReadGpuMode(); gotGpu = true; } catch { }
        try { fn = ReadFnLock(); gotFn = true; } catch { }
        try { tp = ReadTouchpadLock(); gotTp = true; } catch { }

        lock (_cacheLock)
        {
            if (gotThermal) { _cachedThermalMode = thermal; _thermalModeTime = DateTime.UtcNow; }
            if (gotGpu) { _cachedGpuMode = gpu; _gpuModeTime = DateTime.UtcNow; }
            if (gotFn) { _cachedFnLock = fn; _fnLockTime = DateTime.UtcNow; }
            if (gotTp) { _cachedTouchpadLock = tp; _touchpadLockTime = DateTime.UtcNow; }
        }
    }

    private byte ReadThermalMode()
    {
        var input = new byte[32];
        input[1] = 250; // Get
        input[3] = 8;   // SystemPerMode
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)0;
    }

    private byte ReadGpuMode()
    {
        var input = new byte[32];
        input[1] = 250; // Get
        input[3] = 9;   // GPUMode
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)0;
    }

    private byte ReadFnLock()
    {
        var input = new byte[32];
        input[1] = 250;
        input[3] = 11;
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)0;
    }

    private byte ReadTouchpadLock()
    {
        var input = new byte[32];
        input[1] = 250;
        input[3] = 12;
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)0;
    }

    public bool SetThermalMode(byte mode)
    {
        try
        {
            var input = new byte[32];
            input[1] = 251; // Set
            input[3] = 8;   // SystemPerMode
            input[4] = mode;
            CallMethod(input);
            lock (_cacheLock) { _cachedThermalMode = mode; _thermalModeTime = DateTime.UtcNow; }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- GPUMode (?? 9) ----
    public byte GetGpuMode()
    {
        EnsureCacheFresh();
        lock (_cacheLock) return _cachedGpuMode;
    }

    public bool SetGpuMode(byte mode)
    {
        try
        {
            var input = new byte[32];
            input[1] = 251; // Set
            input[3] = 9;   // GPUMode
            input[4] = mode;
            CallMethod(input);
            lock (_cacheLock) { _cachedGpuMode = mode; _gpuModeTime = DateTime.UtcNow; }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>同步读取 GPU 模式（绕过缓存）——启动恢复流程等必须拿实时值的场景。</summary>
    public byte GetGpuModeFresh()
    {
        try
        {
            var v = ReadGpuMode();
            lock (_cacheLock) { _cachedGpuMode = v; _gpuModeTime = DateTime.UtcNow; }
            return v;
        }
        catch
        {
            EnsureCacheFresh();
            lock (_cacheLock) return _cachedGpuMode;
        }
    }

    // ---- FnLock (?? 11) ----
    public byte GetFnLock()
    {
        EnsureCacheFresh();
        lock (_cacheLock) return _cachedFnLock;
    }

    public bool SetFnLock(bool enabled)
    {
        try
        {
            var input = new byte[32];
            input[1] = 251;
            input[3] = 11;
            input[4] = enabled ? (byte)1 : (byte)0;
            CallMethod(input);
            lock (_cacheLock) { _cachedFnLock = enabled ? (byte)1 : (byte)0; _fnLockTime = DateTime.UtcNow; }
            return true;
        }
        catch { return false; }
    }

    // ---- TPLock / TouchpadLock (?? 12) ----
    public byte GetTouchpadLock()
    {
        EnsureCacheFresh();
        lock (_cacheLock) return _cachedTouchpadLock;
    }

    public bool SetTouchpadLock(bool locked)
    {
        try
        {
            var input = new byte[32];
            input[1] = 251;
            input[3] = 12;
            input[4] = locked ? (byte)1 : (byte)0;
            CallMethod(input);
            lock (_cacheLock) { _cachedTouchpadLock = locked ? (byte)1 : (byte)0; _touchpadLockTime = DateTime.UtcNow; }
            return true;
        }
        catch { return false; }
    }

    // ---- Fan control (Bellator protocol: data[4]=FanType, data[5]=value) ----
    // FanType: 0=CPUGPUFan(大扇), 1=SYSFan(小扇)
    public bool SetFanManual(byte fanType, bool enable)
    {
        try
        {
            // MaxFanSwitch(20): data[4]=FanType, data[5]=enable
            var input = new byte[32];
            input[1] = 251;
            input[3] = 20;
            input[4] = fanType;
            input[5] = enable ? (byte)1 : (byte)0;
            CallMethod(input);
            return true;
        }
        catch { return false; }
    }

    public bool SetFanSpeed(byte fanType, byte speed)
    {
        try
        {
            // MaxFanSpeed(21): data[4]=FanType, data[5]=RPM/100
            var input = new byte[32];
            input[1] = 251;
            input[3] = 21;
            input[4] = fanType;
            input[5] = speed;
            CallMethod(input);
            return true;
        }
        catch { return false; }
    }

    // ---- Fan status read (Bellator protocol GET) ----
    public bool GetFanManualEnabled()
    {
        try
        {
            var input = new byte[32];
            input[1] = 250; // GET
            input[3] = 20;  // MaxFanSwitch
            input[4] = 0;   // FanType 0 = 大扇
            var result = CallMethod(input);
            return result.Length > 5 && result[5] == 1;
        }
        catch { return false; }
    }

    public byte GetFanSpeed(byte fanType)
    {
        try
        {
            var input = new byte[32];
            input[1] = 250; // GET
            input[3] = 21;  // MaxFanSpeed
            input[4] = fanType;
            var result = CallMethod(input);
            return result.Length > 5 ? result[5] : (byte)0;
        }
        catch { return 0; }
    }

    // ---- Generic raw command (method number + optional value) ----
    public byte[] SendRawCommand(byte method, byte? value = null)
    {
        var input = new byte[32];
        input[1] = value.HasValue ? (byte)251 : (byte)250; // Set or Get
        input[3] = method;
        if (value.HasValue) input[4] = value.Value;
        return CallMethod(input);
    }
}
