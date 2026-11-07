// SPDX-License-Identifier: MIT
//
// RyzenSmu — AMD SMU 邮箱协议引擎
// 移植自 UXTU RyzenSmu.cs / RyzenSMU.cs
// 依赖 PawnIoDevice + RyzenSMU.bin 进行底层寄存器读写

using System.Threading;

namespace Douzhanzhe.HAL;

/// <summary>SMU 邮箱协议引擎</summary>
public sealed class RyzenSmu : IDisposable
{
    // ---- 邮箱定义 ----
    public sealed class Mailbox
    {
        public string Name { get; }
        public uint MaxArgs { get; set; } = 6;
        public uint MsgAddr { get; set; }
        public uint RspAddr { get; set; }
        public uint ArgAddr { get; set; }
        public bool IsValid => MaxArgs > 0 && MsgAddr != 0 && RspAddr != 0 && ArgAddr != 0;

        public Mailbox(string name) => Name = name;
    }

    // ---- SMU 命令状态 ----
    public enum SmuStatus : byte
    {
        Ok = 0x01,
        Failed = 0xFF,
        UnknownCmd = 0xFE,
        RejectedPrereq = 0xFD,  // UXTU Status.CMD_REJECTED_PREREQ：重试无用，前置条件不满足
        Busy = 0xFC,
    }

    private const int PollLimit = 8192;
    private const string PciMutexName = @"Global\Access_PCI";

    private readonly PawnIoDevice _pawnIo;
    private readonly Dictionary<string, Mailbox> _mailboxes = new(StringComparer.OrdinalIgnoreCase);
    private Mutex? _pciMutex;

    /// <summary>是否已初始化（邮箱已注册）</summary>
    public bool IsReady { get; private set; }

    public RyzenSmu(PawnIoDevice pawnIo)
    {
        _pawnIo = pawnIo ?? throw new ArgumentNullException(nameof(pawnIo));
    }

    /// <summary>打开全局互斥体</summary>
    public void Open()
    {
        try { _pciMutex = Mutex.OpenExisting(PciMutexName); }
        catch
        {
            try { _pciMutex = new Mutex(false, PciMutexName); }
            catch { _pciMutex = null; }
        }
    }

    /// <summary>注册/替换邮箱</summary>
    public Mailbox RegisterMailbox(string name, uint msgAddr, uint rspAddr, uint argAddr, uint maxArgs = 6)
    {
        var mb = new Mailbox(name)
        {
            MsgAddr = msgAddr,
            RspAddr = rspAddr,
            ArgAddr = argAddr,
            MaxArgs = maxArgs,
        };
        _mailboxes[mb.Name] = mb;
        IsReady = _mailboxes.Values.Any(m => m.IsValid);
        return mb;
    }

    /// <summary>获取已注册的邮箱</summary>
    public Mailbox? GetMailbox(string name)
    {
        _mailboxes.TryGetValue(name, out var mb);
        return mb;
    }

    /// <summary>发送 SMU 命令</summary>
    public SmuStatus SendSmuCommand(Mailbox mailbox, uint message, ref uint[] args)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        if (!mailbox.IsValid || message == 0)
            return SmuStatus.Failed;

        // 获取互斥锁
        if (_pciMutex != null && !WaitForMutex(_pciMutex, 10))
            return SmuStatus.Failed;

        try
        {
            return ExecuteMailboxFlow(mailbox, message, ref args);
        }
        finally
        {
            SafeReleaseMutex(_pciMutex);
        }
    }

    /// <summary>便捷方法: 发 MP1 命令</summary>
    public SmuStatus SendMp1(uint message, ref uint[] args)
    {
        var mb = GetMailbox("MP1");
        return mb != null ? SendSmuCommand(mb, message, ref args) : SmuStatus.Failed;
    }

    /// <summary>便捷方法: 发 RSMU 命令</summary>
    public SmuStatus SendRsmu(uint message, ref uint[] args)
    {
        var mb = GetMailbox("RSMU");
        return mb != null ? SendSmuCommand(mb, message, ref args) : SmuStatus.Failed;
    }

    // ---- 底层寄存器读写（通过 PawnIO RyzenSMU.bin） ----

    /// <summary>读 SMU 32 位寄存器</summary>
    public uint Read32(uint address)
    {
        var result = _pawnIo.Execute("ioctl_read_smu_register", [address], 1);
        return result.Length > 0 ? (uint)result[0] : 0u;
    }

    /// <summary>写 SMU 32 位寄存器</summary>
    public void Write32(uint address, uint value)
    {
        _pawnIo.Execute("ioctl_write_smu_register", [address, value], 0);
    }

    // ---- 邮箱协议核心 ----

    SmuStatus ExecuteMailboxFlow(Mailbox mb, uint message, ref uint[] args)
    {
        // 1. 等待 SMU 空闲（RSP 非零 → 可以接受新命令）
        if (!WaitForResponse(mb.RspAddr))
            return SmuStatus.Failed;

        // 2. 清除响应寄存器
        Write32(mb.RspAddr, 0);

        // 3. 写入参数
        WriteArguments(mb, args);

        // 4. 写入消息 ID → SMU 开始执行
        Write32(mb.MsgAddr, message);

        // 5. 等待 SMU 完成（RSP 变为非零 = 状态码就绪）
        if (!WaitForResponse(mb.RspAddr))
            return SmuStatus.Failed;

        // 6. 读取状态
        var rsp = Read32(mb.RspAddr);
        var status = (SmuStatus)(rsp & 0xFF);

        // 7. 成功时回读参数
        if (status == SmuStatus.Ok)
            ReadArguments(mb, ref args);

        return status;
    }

    bool WaitForResponse(uint rspAddr)
    {
        for (int i = 0; i < PollLimit; i++)
        {
            if (Read32(rspAddr) != 0) return true;
            Thread.SpinWait(1);
        }
        return false;
    }

    void WriteArguments(Mailbox mb, uint[] args)
    {
        var prepared = PrepareArguments(mb, args);
        for (int i = 0; i < prepared.Length; i++)
            Write32(mb.ArgAddr + (uint)(i * 4), prepared[i]);
    }

    void ReadArguments(Mailbox mb, ref uint[] args)
    {
        // 限制回读到实际请求长度
        int count = (int)Math.Min(mb.MaxArgs, (uint)args.Length);
        for (int i = 0; i < count; i++)
            args[i] = Read32(mb.ArgAddr + (uint)(i * 4));
    }

    static uint[] PrepareArguments(Mailbox mb, uint[] args)
    {
        var result = new uint[mb.MaxArgs];
        int copy = Math.Min(args.Length, (int)mb.MaxArgs);
        Array.Copy(args, result, copy);
        // 剩余自动补 0
        return result;
    }

    // ---- 互斥体 ----

    static bool WaitForMutex(Mutex mutex, int timeoutMs)
    {
        try { return mutex.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; }
        catch { return false; }
    }

    static void SafeReleaseMutex(Mutex? mutex)
    {
        try { mutex?.ReleaseMutex(); }
        catch { /* 忽略释放异常 */ }
    }

    // ---- AM5_V1 地址表 ----
    // 8940HX (Dragon Range / Zen 4) 使用的邮箱地址

    public const uint Am5V1_Mp1Msg = 0x3B10530;
    public const uint Am5V1_Mp1Rsp = 0x3B1057C;
    public const uint Am5V1_Mp1Arg = 0x3B109C4;
    public const uint Am5V1_RsmuMsg = 0x03B10524;
    public const uint Am5V1_RsmuRsp = 0x03B10570;
    public const uint Am5V1_RsmuArg = 0x03B10A40;

    /// <summary>初始化 AM5_V1 地址表（8940HX Dragon Range 适用）</summary>
    public void InitAm5V1()
    {
        RegisterMailbox("MP1", Am5V1_Mp1Msg, Am5V1_Mp1Rsp, Am5V1_Mp1Arg);
        RegisterMailbox("RSMU", Am5V1_RsmuMsg, Am5V1_RsmuRsp, Am5V1_RsmuArg);
    }

    public void Dispose()
    {
        _pciMutex?.Dispose();
        _pciMutex = null;
    }
}

/// <summary>8940HX (Dragon Range) SMU 命令定义</summary>
public static class SmuCommands
{
    // MP1 邮箱命令
    public const uint StapmLimit = 0x4f;       // 长时功耗 (STAPM)
    public const uint FastLimit = 0x3e;         // 短时功耗 (PPT fast)
    public const uint SlowLimit = 0x5f;         // 慢速功耗 (PPT slow)
    public const uint TctlTemp = 0x3f;          // 温度墙
    public const uint VrmCurrent = 0x3c;        // VRM 电流 (TDC)
    public const uint VrmMaxCurrent = 0x3d;     // VRM 最大电流 (EDC)
    // ---- Curve Optimizer ----
    // 旧 MP1 路径（本仓库早期迁移方案，保留作为回退）
    public const uint SetCoAll = 0x36;          // MP1 全核 Curve Optimizer
    public const uint SetCoPerCore = 0x35;      // MP1 逐核 Curve Optimizer
    // Dragon Range / AM5 RSMU 路径（UXTU 0229d72 RyzenSmu.cs Socket_AM5_V1）：
    //   ("set-coper", false, 0x06) / ("set-coall", false, 0x07)，IsMp1=false 即 RSMU
    public const uint SetCoAllRsmu = 0x07;
    public const uint SetCoPerCoreRsmu = 0x06;
    // 只读能力查询（UXTU 命令表有定义，产品未调用；用于不做写入的探针）
    public const uint GetCoPerOptions = 0xD5;
    public const uint GetPboFusedPowerLimit = 0xDC;

    public const uint SetBoostLimit = 0x2b;     // 最大 Boost 频率限制
    public const uint PowerSaving = 0x31;       // 节能模式
    public const uint MaxPerformance = 0x32;    // 性能模式
}
