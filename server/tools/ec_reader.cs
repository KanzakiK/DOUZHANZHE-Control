using System;
using System.Runtime.InteropServices;
using System.Threading;
class EcReader
{
    // inpoutx64.dll — MIT 许可证开源驱动，自动加载驱动无需显式初始化
    [DllImport("inpoutx64.dll", EntryPoint = "IsInpOutDriverOpen", CallingConvention = CallingConvention.StdCall)]
    static extern bool IsInpOutDriverOpen();
    [DllImport("inpoutx64.dll", EntryPoint = "Inp32", CallingConvention = CallingConvention.StdCall)]
    static extern short Inp32(short port);
    [DllImport("inpoutx64.dll", EntryPoint = "Out32", CallingConvention = CallingConvention.StdCall)]
    static extern void Out32(short port, short data);

    static void S(int m = 2) { Thread.Sleep(m); }

    // EC 读寄存器协议: 0x80 → 0x66, 地址 → 0x62, 数据 ← 0x62
    static byte R(byte a)
    {
        Out32(0x66, 0x80); S();
        Out32(0x62, a);    S(5);
        return (byte)(Inp32(0x62) & 0xFF);
    }

    static int Read16(byte h, byte l)
    {
        int v = (R(h) << 8) | R(l);
        Thread.Sleep(4);
        return v;
    }

    static int ReadFan(byte h, byte l, int maxRpm)
    {
        for (int i = 0; i < 10; i++)
        {
            if (i > 0) Thread.Sleep(15);
            int v = Read16(h, l);
            if (v > 50 && v <= maxRpm) return v;
        }
        return 0;
    }

    static int ReadTemp()
    {
        for (int i = 0; i < 5; i++)
        {
            if (i > 0) Thread.Sleep(10);
            int v = R(0x1C);
            if (v > 20 && v < 120) return v;
        }
        return 0;
    }

    static int Main(string[] args)
    {
        if (!IsInpOutDriverOpen())
        {
            // 首次调用会触发驱动安装，等一会再试
            Thread.Sleep(500);
            if (!IsInpOutDriverOpen())
            {
                Console.Write("INITFAIL");
                return 1;
            }
        }
        try
        {
            string m = args.Length > 0 ? args[0] : "";
            int r = 0;
            if (m == "cpu") r = ReadFan(0x9D, 0x9E, 4400);
            else if (m == "gpu") r = ReadFan(0x96, 0x97, 8200);
            else if (m == "temp") r = ReadTemp();
            Console.Write(r.ToString());
            return r > 0 ? 0 : 2;
        }
        catch (Exception ex) { Console.Write("ERR:" + ex.Message); return 2; }
    }
}