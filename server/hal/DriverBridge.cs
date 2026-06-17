// SPDX-License-Identifier: MIT
using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Diagnostics;
using System.Threading;

namespace Douzhanzhe.HAL;

public sealed class DriverBridge : IDisposable
{
    [DllImport("inpoutx64.dll", EntryPoint = "IsInpOutDriverOpen", CallingConvention = CallingConvention.StdCall)]
    static extern bool IsInpOutDriverOpenNative();
    [DllImport("inpoutx64.dll", EntryPoint = "DlPortReadPortUchar", CallingConvention = CallingConvention.StdCall)]
    static extern byte ReadPortUcharNative(short port);
    [DllImport("inpoutx64.dll", EntryPoint = "DlPortWritePortUchar", CallingConvention = CallingConvention.StdCall)]
    static extern void WritePortUcharNative(short port, byte data);
    [DllImport("inpoutx64.dll", EntryPoint = "Out32", CallingConvention = CallingConvention.StdCall)]
    static extern void Out32Native(short port, int data);
    [DllImport("inpoutx64.dll", EntryPoint = "Inp32", CallingConvention = CallingConvention.StdCall)]
    static extern int Inp32Native(short port);
    [DllImport("inpoutx64.dll", EntryPoint = "MapPhysToLin", CallingConvention = CallingConvention.StdCall)]
    static extern bool MapPhysToLinNative(ulong p, uint s, out IntPtr l);
    [DllImport("inpoutx64.dll", EntryPoint = "GetPhysLong", CallingConvention = CallingConvention.StdCall)]
    static extern bool GetPhysLongNative(out uint v, ulong p);
    [DllImport("inpoutx64.dll", EntryPoint = "SetPhysLong", CallingConvention = CallingConvention.StdCall)]
    static extern bool SetPhysLongNative(ulong p, uint v);

    public const uint EC_BASE = 0xFE800400;
    public const uint EC_SIZE = 0xFF;
    static readonly Lazy<DriverBridge> _instance = new(() => new DriverBridge(), LazyThreadSafetyMode.ExecutionAndPublication);
    readonly object _lock = new();
    readonly object _ecLock = new();
    volatile bool _init, _dis, _driverOk;
    volatile IntPtr _ecMap;
    volatile bool _ecOk;
    volatile int _recovering; // 睡眠恢复中标志，防止并发重入
    DriverBridge() {}
    public static DriverBridge Instance => _instance.Value;

    public void Init(int r = 1000)
    {
        if (_init) return;
        lock(_lock) {
            if (_init) return;
            if (_dis) throw new ObjectDisposedException("");
            try {
                Out32Native(0x80, 0);
                var sw = Stopwatch.StartNew();
                while (!IsInpOutDriverOpenNative() && sw.ElapsedMilliseconds < r) Thread.Sleep(50);
                if (!IsInpOutDriverOpenNative()) {
                    AppLog.Write("DriverBridge", "inpoutx64 驱动不可用，硬件访问降级为安全默认值");
                    _init = true; // 标记已尝试初始化，不再重试
                    return;
                }
                if (MapPhysToLinNative(EC_BASE, EC_SIZE, out var m)) { _ecMap = m; _ecOk = true; }
                _driverOk = true;
                _init = true;
            } catch (Exception ex) {
                AppLog.Write("DriverBridge", $"驱动初始化异常: {ex.Message}，硬件访问降级");
                _init = true; // 标记已尝试，不再重试
            }
        }
    }
    public bool Ready => _driverOk;
    public void Dispose() { _dis = true; _init = false; _driverOk = false; _ecMap = IntPtr.Zero; _ecOk = false; }

    /// <summary>
    /// 重置驱动状态，允许下次访问时重新初始化。
    /// 用于系统从睡眠/休眠恢复后，内核驱动可能已失效的场景。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _init = false;
            _driverOk = false;
            _ecMap = IntPtr.Zero;
            _ecOk = false;
            AppLog.Write("DriverBridge", "已重置，下次访问将重新初始化驱动");
        }
    }

    /// <summary>
    /// 系统从睡眠/休眠恢复时调用：重置驱动状态并尝试重新初始化。
    /// inpoutx64 内核驱动在 S3/S4 恢复后可能已失效，必须重新建立映射。
    /// </summary>
    public void RecoverAfterSleep()
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;
        try
        {
            lock (_lock)
            {
                AppLog.Write("DriverBridge", "睡眠恢复: 重置驱动状态...");
                _init = false;
                _driverOk = false;
                _ecMap = IntPtr.Zero;
                _ecOk = false;
            }
            // 等待内核驱动稳定（inpoutx64 在 S3 恢复后需要时间重新就绪）
            Thread.Sleep(1500);
            Init(3000); // 给更长的超时，让驱动有机会恢复
            AppLog.Write("DriverBridge", $"睡眠恢复完成: Ready={_driverOk}, EcOk={_ecOk}");
        }
        finally { _recovering = 0; }
    }

    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    public byte ReadPhys(ulong a)
    {
        Ensure();
        if (!_driverOk) return 0;
        try
        {
            if (_ecOk && a >= EC_BASE && a < (ulong)(EC_BASE + EC_SIZE))
                unsafe { return *(byte*)((nint)((long)_ecMap + (long)(a - EC_BASE))); }
            if (MapPhysToLinNative(a, 1, out var l)) unsafe { return *(byte*)l; }
            if (a <= 0xFFFFFFFF && GetPhysLongNative(out var v, a)) return (byte)(v & 0xFF);
        }
        catch (AccessViolationException)
        {
            // 睡眠恢复后映射指针失效，降级到 GetPhysLong 或返回安全默认值
            AppLog.Write("DriverBridge", "ReadPhys: AccessViolation, 尝试 GetPhysLong 降级");
            _ecOk = false; _ecMap = IntPtr.Zero; // 废弃失效的映射
            if (a <= 0xFFFFFFFF && GetPhysLongNative(out var v2, a)) return (byte)(v2 & 0xFF);
            return 0;
        }
        catch (SEHException)
        {
            AppLog.Write("DriverBridge", "ReadPhys: SEHException, 驱动可能已失效");
            _driverOk = false;
            return 0;
        }
        throw new InvalidOperationException("读失败");
    }
    public uint ReadPhys32(ulong a)
    {
        Ensure();
        if (!_driverOk) return 0;
        if (a <= 0xFFFFFFFF && GetPhysLongNative(out var v, a)) return v;
        throw new InvalidOperationException("读32失败");
    }
    public void WritePhys(ulong a, byte v)
    {
        Ensure();
        if (!_driverOk) return;
        // 不经过预映射缓存，直接 SetPhysLong（缓存写入对某些地址无效）
        if (a <= 0xFFFFFFFF) { SetPhysLongNative(a, v); return; }
        // 大地址兜底：动态映射
        if (MapPhysToLinNative(a, 1, out var l)) { unsafe { *(byte*)l = v; } return; }
        throw new InvalidOperationException("写失败");
    }
    public void WritePhys32(ulong a, uint v)
    {
        Ensure();
        if (!_driverOk) return;
        if (a <= 0xFFFFFFFF) { SetPhysLongNative(a, v); return; }
        throw new InvalidOperationException("写32失败");
    }
    /// <summary>通过 EC IO 协议写单个字节（端口 0x62/0x66）</summary>
    public void WritePhysByte(ulong a, byte v) => WritePhys(a, v);
    public void WriteBit(ulong a, int b, bool s)
    { lock (_lock) { var x = ReadPhys(a); if(s) x|=(byte)(1<<b); else x&=unchecked((byte)~(1<<b)); WritePhys(a,x); } }
    public ushort ReadWord(ulong a) { return (ushort)((ReadPhys(a+1)<<8)|ReadPhys(a)); }

    public byte ReadIo(short p)
    {
        Ensure(); if (!_driverOk) return 0;
        try { return ReadPortUcharNative(p); }
        catch (SEHException) { _driverOk = false; return 0; }
    }
    public void WriteIo(short p, byte v)
    {
        Ensure(); if (!_driverOk) return;
        try { WritePortUcharNative(p, v); }
        catch (SEHException) { _driverOk = false; }
    }
    public int ReadIo32(short p)
    {
        Ensure(); if (!_driverOk) return 0;
        try { return Inp32Native(p); }
        catch (SEHException) { _driverOk = false; return 0; }
    }
    public void WriteIo32(short p, int v)
    {
        Ensure(); if (!_driverOk) return;
        try { Out32Native(p, v); }
        catch (SEHException) { _driverOk = false; }
    }

    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    public byte ReadEc(byte r)
    {
        Ensure();
        if (!_driverOk) return 0;
        lock(_ecLock) {
            try
            {
                if (!WaitEcReady()) return 0; // 发命令前检查 IBF
                WritePortUcharNative(0x66, 0x80); Thread.Sleep(2);
                WritePortUcharNative(0x62, r); Thread.Sleep(5);
                return ReadPortUcharNative(0x62);
            }
            catch (SEHException)
            {
                AppLog.Write("DriverBridge", "ReadEc: SEHException, 驱动可能已失效");
                _driverOk = false;
                return 0;
            }
        }
    }
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    public void WriteEc(byte r, byte v)
    {
        Ensure();
        if (!_driverOk) return;
        lock(_ecLock) {
            try
            {
                // 标准 EC 写入协议（与旧版 ec_writer.cs 一致）
                // 1. 等待 IBF 为空，发送写入命令
                if (!WaitEcReady()) return;
                WritePortUcharNative(0x66, 0x81); Thread.Sleep(5);
                // 2. 等待 IBF 为空，发送寄存器地址
                if (!WaitEcReady()) return;
                WritePortUcharNative(0x62, r); Thread.Sleep(5);
                // 3. 等待 IBF 为空，发送数据
                if (!WaitEcReady()) return;
                WritePortUcharNative(0x62, v); Thread.Sleep(10);
            }
            catch (SEHException)
            {
                AppLog.Write("DriverBridge", "WriteEc: SEHException, 驱动可能已失效");
                _driverOk = false;
            }
        }
    }
    /// <summary>等待 EC IBF (Input Buffer Full) 为空，返回是否成功</summary>
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    bool WaitEcReady(int timeoutMs = 200)
    {
        if (!_driverOk) return false;
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if ((ReadPortUcharNative(0x66) & 0x02) == 0) return true;
                Thread.Sleep(1);
            }
            AppLog.Write("DriverBridge", $"WaitEcReady: 超时 {timeoutMs}ms");
            return false;
        }
        catch (SEHException) { _driverOk = false; return false; }
    }
    void Ensure() { if (_dis) throw new ObjectDisposedException(""); if (!_init) Init(); }
}
