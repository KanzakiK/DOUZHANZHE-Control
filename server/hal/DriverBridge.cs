// SPDX-License-Identifier: MIT
using System;
using System.Runtime.InteropServices;
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
    bool _init, _dis, _driverOk;
    IntPtr _ecMap;
    bool _ecOk;
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
                    Console.WriteLine("[DriverBridge] inpoutx64 驱动不可用，硬件访问降级为安全默认值");
                    _init = true; // 标记已尝试初始化，不再重试
                    return;
                }
                if (MapPhysToLinNative(EC_BASE, EC_SIZE, out var m)) { _ecMap = m; _ecOk = true; }
                _driverOk = true;
                _init = true;
            } catch (Exception ex) {
                Console.WriteLine($"[DriverBridge] 驱动初始化异常: {ex.Message}，硬件访问降级");
                _init = true; // 标记已尝试，不再重试
            }
        }
    }
    public bool Ready => _driverOk;
    public void Dispose() { _dis = true; _init = false; _driverOk = false; _ecMap = IntPtr.Zero; _ecOk = false; }

    public byte ReadPhys(ulong a)
    {
        Ensure();
        if (!_driverOk) return 0;
        if (_ecOk && a >= EC_BASE && a < (ulong)(EC_BASE + EC_SIZE))
            unsafe { return *(byte*)((nint)((long)_ecMap + (long)(a - EC_BASE))); }
        if (MapPhysToLinNative(a, 1, out var l)) unsafe { return *(byte*)l; }
        if (a <= 0xFFFFFFFF && GetPhysLongNative(out var v, a)) return (byte)(v & 0xFF);
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
    { var x = ReadPhys(a); if(s) x|=(byte)(1<<b); else x&=unchecked((byte)~(1<<b)); WritePhys(a,x); }
    public ushort ReadWord(ulong a) { return (ushort)((ReadPhys(a+1)<<8)|ReadPhys(a)); }

    public byte ReadIo(short p) { Ensure(); if (!_driverOk) return 0; return ReadPortUcharNative(p); }
    public void WriteIo(short p, byte v) { Ensure(); if (!_driverOk) return; WritePortUcharNative(p, v); }
    public int ReadIo32(short p) { Ensure(); if (!_driverOk) return 0; return Inp32Native(p); }
    public void WriteIo32(short p, int v) { Ensure(); if (!_driverOk) return; Out32Native(p, v); }

    public byte ReadEc(byte r)
    {
        Ensure();
        if (!_driverOk) return 0;
        lock(_ecLock) {
            WritePortUcharNative(0x66, 0x80); Thread.Sleep(2);
            WritePortUcharNative(0x62, r); Thread.Sleep(5);
            return ReadPortUcharNative(0x62);
        }
    }
    public void WriteEc(byte r, byte v)
    {
        Ensure();
        if (!_driverOk) return;
        lock(_ecLock) {
            // 标准 EC 写入协议（与旧版 ec_writer.cs 一致）
            // 1. 等待 IBF 为空，发送写入命令
            WaitEcReady();
            WritePortUcharNative(0x66, 0x81); Thread.Sleep(5);
            // 2. 等待 IBF 为空，发送寄存器地址
            WaitEcReady();
            WritePortUcharNative(0x62, r); Thread.Sleep(5);
            // 3. 等待 IBF 为空，发送数据
            WaitEcReady();
            WritePortUcharNative(0x62, v); Thread.Sleep(10);
        }
    }
    /// <summary>等待 EC IBF (Input Buffer Full) 为空</summary>
    void WaitEcReady()
    {
        if (!_driverOk) return;
        for (int i = 0; i < 100; i++)
        {
            if ((ReadPortUcharNative(0x66) & 0x02) == 0) return;
            Thread.Sleep(1);
        }
    }
    void Ensure() { if (_dis) throw new ObjectDisposedException(""); if (!_init) Init(); }
}
