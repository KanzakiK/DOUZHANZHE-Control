using System;
using System.Runtime.InteropServices;
using System.Threading;

class TestEc
{
    [DllImport("inpoutx64.dll", EntryPoint = "IsInpOutDriverOpen", CallingConvention = CallingConvention.StdCall)]
    static extern bool IsInpOutDriverOpenNative();
    [DllImport("inpoutx64.dll", EntryPoint = "DlPortReadPortUchar", CallingConvention = CallingConvention.StdCall)]
    static extern byte ReadPortUcharNative(short port);
    [DllImport("inpoutx64.dll", EntryPoint = "DlPortWritePortUchar", CallingConvention = CallingConvention.StdCall)]
    static extern void WritePortUcharNative(short port, byte data);

    static void WaitIBF()
    {
        for (int i = 0; i < 100; i++)
        {
            if ((ReadPortUcharNative(0x66) & 0x02) == 0) return;
            Thread.Sleep(1);
        }
    }

    static byte ReadEc(byte reg)
    {
        WritePortUcharNative(0x66, 0x80);
        Thread.Sleep(2);
        WritePortUcharNative(0x62, reg);
        Thread.Sleep(5);
        return ReadPortUcharNative(0x62);
    }

    static void WriteEc(byte reg, byte val)
    {
        WaitIBF(); WritePortUcharNative(0x66, 0x81); Thread.Sleep(5);
        WaitIBF(); WritePortUcharNative(0x62, reg); Thread.Sleep(5);
        WaitIBF(); WritePortUcharNative(0x62, val); Thread.Sleep(10);
    }

    static void Main()
    {
        Console.WriteLine("=== EC 0x5F 最小测试 ===");
        if (!IsInpOutDriverOpenNative()) { Console.WriteLine("ERR: 驱动未加载"); return; }
        Console.WriteLine("OK: inpoutx64 驱动就绪");

        for (int t = 0; t < 3; t++)
        {
            byte val = (byte)(25 + t * 5);
            byte before = ReadEc(0x5F);
            Console.WriteLine("轮" + t + ": 0x5F当前=" + before);
            Console.WriteLine("写入" + val + "...");
            WriteEc(0x5F, val);
            Thread.Sleep(1000);
            byte after = ReadEc(0x5F);
            Console.WriteLine("回读=" + after + " 匹配=" + (after == val));
        }
        Console.WriteLine("=== 完成，请观察风扇物理转速 ===");
    }
}
