using System;
using System.Runtime.InteropServices;
using System.Threading;

// ec_writer.exe <reg_hex> <value_byte>
//   例: ec_writer.exe 0xB2 80   — 写 0x80 到寄存器 0xB2
//
// 使用 WinRing0x64.dll 通过标准 EC I/O 端口 (0x66/0x62) 写入 EC 寄存器。
// 写入协议:
//   1. 检查 IBF 为空 (轮询 0x66 的 bit 1)
//   2. 写 0x81 到 0x66 (EC RAM 写入命令)
//   3. 写寄存器地址到 0x62
//   4. 等待 EC 就绪
//   5. 写数据到 0x62
//
// 注意: 并非所有 EC 寄存器都支持写入。风扇控制寄存器因 OEM 不同而异，
//       需要逆向分析 EC 固件或通过试错发现。

class EcWriter
{
    [DllImport("WinRing0x64.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern bool InitializeOls();

    [DllImport("WinRing0x64.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void DeinitializeOls();

    [DllImport("WinRing0x64.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern byte ReadIoPortByte(ushort port);

    [DllImport("WinRing0x64.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WriteIoPortByte(ushort port, byte value);

    /// 等待 EC IBF (Input Buffer Full) 为空
    static void WaitForIBF()
    {
        for (int i = 0; i < 100; i++)
        {
            byte status = ReadIoPortByte(0x66);
            if ((status & 0x02) == 0) return; // IBF 为空
            Thread.Sleep(1);
        }
    }

    /// 等待 EC OBF (Output Buffer Full) 有数据
    static void WaitForOBF()
    {
        for (int i = 0; i < 100; i++)
        {
            byte status = ReadIoPortByte(0x66);
            if ((status & 0x01) != 0) return; // OBF 有数据
            Thread.Sleep(1);
        }
    }

    /// 通过标准 EC 接口写入寄存器
    static bool EcWrite(byte address, byte data)
    {
        try
        {
            // 1. 发送写入命令
            WaitForIBF();
            WriteIoPortByte(0x66, 0x81); // EC RAM Write
            Thread.Sleep(5);

            // 2. 发送寄存器地址
            WaitForIBF();
            WriteIoPortByte(0x62, address);
            Thread.Sleep(5);

            // 3. 发送要写入的数据
            WaitForIBF();
            WriteIoPortByte(0x62, data);
            Thread.Sleep(10);

            // 4. 验证: 读回该寄存器
            WaitForIBF();
            WriteIoPortByte(0x66, 0x80); // EC RAM Read
            Thread.Sleep(5);
            WaitForIBF();
            WriteIoPortByte(0x62, address);
            Thread.Sleep(5);
            WaitForOBF();
            byte readBack = ReadIoPortByte(0x62);

            return readBack == data;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(string.Format("ERR:{0}", ex.Message));
            return false;
        }
    }

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: ec_writer.exe <reg_hex> <data_byte>");
            Console.Error.WriteLine("  例: ec_writer.exe 0xB2 80");
            return 1;
        }

        if (!InitializeOls())
        {
            Console.Error.WriteLine("INITFAIL");
            return 1;
        }

        try
        {
            // 解析参数
            string regStr = args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? args[0] : "0x" + args[0];
            byte address = Convert.ToByte(regStr, 16);
            byte data = Convert.ToByte(args[1]);

            bool success = EcWrite(address, data);

            if (success)
            {
                Console.Write(string.Format("OK: Wrote 0x{0:X2} to reg 0x{1:X2}", data, address));
                return 0;
            }
            else
            {
                Console.Error.Write(string.Format("FAIL: Write 0x{0:X2} to reg 0x{1:X2} failed (read-back mismatch)", data, address));
                return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.Write(string.Format("ERR:{0}", ex.Message));
            return 2;
        }
        finally
        {
            DeinitializeOls();
        }
    }
}
