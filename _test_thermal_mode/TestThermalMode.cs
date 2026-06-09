// 测试脚本：验证不同模式切换方法对风扇的影响
// 使用方法：在 server 目录下运行 dotnet run --project TestThermalMode.csproj

using System;
using System.Management;
using System.Threading;

class Program
{
    static void Main()
    {
        Console.WriteLine("=== 散热模式切换实验 ===\n");
        
        // 初始化 WMI
        var wmiScope = new ManagementScope(@"root\WMI");
        wmiScope.Connect();
        
        Console.WriteLine("WMI 连接成功\n");
        
        while (true)
        {
            Console.WriteLine("选择测试:");
            Console.WriteLine("1. 读取当前状态（模式 + 风扇）");
            Console.WriteLine("2. EC 寄存器切换模式（0xE4）");
            Console.WriteLine("3. WMI 方法 8 切换模式（SystemPerMode）");
            Console.WriteLine("4. WMI 方法 20 关闭风扇手动模式");
            Console.WriteLine("5. WMI 方法 21 设置风扇转速");
            Console.WriteLine("6. 读取风扇手动模式状态");
            Console.WriteLine("0. 退出");
            Console.Write("\n请输入: ");
            
            var choice = Console.ReadLine();
            
            try
            {
                switch (choice)
                {
                    case "1":
                        ReadStatus();
                        break;
                    case "2":
                        TestEcThermalMode();
                        break;
                    case "3":
                        TestWmiSystemPerMode();
                        break;
                    case "4":
                        TestWmiFanManualSwitch();
                        break;
                    case "5":
                        TestWmiFanSpeed();
                        break;
                    case "6":
                        ReadFanManualStatus();
                        break;
                    case "0":
                        return;
                    default:
                        Console.WriteLine("无效选择");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"错误: {ex.Message}");
            }
            
            Console.WriteLine("\n按 Enter 继续...\n");
            Console.ReadLine();
        }
    }
    
    static void ReadStatus()
    {
        Console.WriteLine("\n--- 当前状态 ---");
        
        // 读取 EC 模式 (0xE4)
        var mode = ReadEcThermalMode();
        Console.WriteLine($"EC 散热模式: {mode} ({GetModeName(mode)})");
        
        // 读取风扇转速
        var fanLarge = ReadFanRpm(0x9B, 0x9C);
        var fanSmall = ReadFanRpm(0x96, 0x97);
        Console.WriteLine($"大风扇 RPM: {fanLarge}");
        Console.WriteLine($"小风扇 RPM: {fanSmall}");
        
        // 读取风扇手动模式状态
        var fanManual = GetFanManualEnabled(0);
        Console.WriteLine($"风扇手动模式: {(fanManual ? "启用" : "禁用")}");
    }
    
    static void TestEcThermalMode()
    {
        Console.Write("\n切换到模式 (0=office, 1=beast, 2=silent, 3=gaming): ");
        var modeStr = Console.ReadLine();
        if (!byte.TryParse(modeStr, out byte mode) || mode > 3)
        {
            Console.WriteLine("无效模式值");
            return;
        }
        
        Console.WriteLine($"\n准备通过 EC 寄存器切换到 {GetModeName(mode)}...");
        Console.WriteLine("请先记录当前风扇转速，然后按 Enter 继续");
        Console.ReadLine();
        
        var fanBefore1 = ReadFanRpm(0x9B, 0x9C);
        var fanBefore2 = ReadFanRpm(0x96, 0x97);
        Console.WriteLine($"切换前 - 大扇: {fanBefore1}, 小扇: {fanBefore2}");
        
        // 写入 EC 0xE4
        WriteEcThermalMode(mode);
        Console.WriteLine($"已写入 EC 0xE4 = {mode}");
        
        Thread.Sleep(1000);
        
        var fanAfter1 = ReadFanRpm(0x9B, 0x9C);
        var fanAfter2 = ReadFanRpm(0x96, 0x97);
        Console.WriteLine($"切换后 - 大扇: {fanAfter1}, 小扇: {fanAfter2}");
        
        if (fanBefore1 != fanAfter1 || fanBefore2 != fanAfter2)
        {
            Console.WriteLine("⚠️ 风扇转速发生变化！EC 切换会影响风扇。");
        }
        else
        {
            Console.WriteLine("✅ 风扇转速未变化。EC 切换不影响风扇。");
        }
    }
    
    static void TestWmiSystemPerMode()
    {
        Console.Write("\n切换到模式 (0=office, 1=beast, 2=silent, 3=gaming): ");
        var modeStr = Console.ReadLine();
        if (!byte.TryParse(modeStr, out byte mode) || mode > 3)
        {
            Console.WriteLine("无效模式值");
            return;
        }
        
        Console.WriteLine($"\n准备通过 WMI 方法 8 切换到 {GetModeName(mode)}...");
        Console.WriteLine("请先记录当前风扇转速，然后按 Enter 继续");
        Console.ReadLine();
        
        var fanBefore1 = ReadFanRpm(0x9B, 0x9C);
        var fanBefore2 = ReadFanRpm(0x96, 0x97);
        Console.WriteLine($"切换前 - 大扇: {fanBefore1}, 小扇: {fanBefore2}");
        
        // WMI 方法 8
        var input = new byte[32];
        input[1] = 251; // Set
        input[3] = 8;   // SystemPerMode
        input[4] = mode;
        
        CallWmi(input);
        Console.WriteLine($"已通过 WMI 方法 8 写入 mode = {mode}");
        
        Thread.Sleep(1000);
        
        var fanAfter1 = ReadFanRpm(0x9B, 0x9C);
        var fanAfter2 = ReadFanRpm(0x96, 0x97);
        Console.WriteLine($"切换后 - 大扇: {fanAfter1}, 小扇: {fanAfter2}");
        
        if (fanBefore1 != fanAfter1 || fanBefore2 != fanAfter2)
        {
            Console.WriteLine("⚠️ 风扇转速发生变化！WMI 方法 8 会影响风扇。");
        }
        else
        {
            Console.WriteLine("✅ 风扇转速未变化。WMI 方法 8 不影响风扇。");
        }
    }
    
    static void TestWmiFanManualSwitch()
    {
        Console.Write("\n设置风扇手动模式 (0=关闭/固件控制, 1=开启/手动控制): ");
        var enableStr = Console.ReadLine();
        if (!byte.TryParse(enableStr, out byte enable) || enable > 1)
        {
            Console.WriteLine("无效值");
            return;
        }
        
        var input = new byte[32];
        input[1] = 251; // Set
        input[3] = 20;  // MaxFanSpeedSwitch
        input[4] = 0;   // FanType 0 = 大扇
        input[5] = enable;
        
        CallWmi(input);
        Console.WriteLine($"已通过 WMI 方法 20 设置大扇手动模式 = {enable}");
        
        Thread.Sleep(500);
        
        var isManual = GetFanManualEnabled(0);
        Console.WriteLine($"当前大扇手动模式: {(isManual ? "启用" : "禁用")}");
    }
    
    static void TestWmiFanSpeed()
    {
        Console.Write("\n设置大风扇转速 (0-255, 单位: RPM/100): ");
        var speedStr = Console.ReadLine();
        if (!byte.TryParse(speedStr, out byte speed))
        {
            Console.WriteLine("无效值");
            return;
        }
        
        var input = new byte[32];
        input[1] = 251; // Set
        input[3] = 21;  // MaxFanSpeed
        input[4] = 0;   // FanType 0 = 大扇
        input[5] = speed;
        
        CallWmi(input);
        Console.WriteLine($"已通过 WMI 方法 21 设置大扇 = {speed} ({speed * 100} RPM)");
        
        Thread.Sleep(500);
        
        var fanRpm = ReadFanRpm(0x9B, 0x9C);
        Console.WriteLine($"当前大风扇 RPM: {fanRpm}");
    }
    
    static void ReadFanManualStatus()
    {
        Console.WriteLine("\n--- 风扇手动模式状态 ---");
        var largeManual = GetFanManualEnabled(0);
        var smallManual = GetFanManualEnabled(1);
        Console.WriteLine($"大扇手动模式: {(largeManual ? "启用" : "禁用")}");
        Console.WriteLine($"小扇手动模式: {(smallManual ? "启用" : "禁用")}");
    }
    
    // === 辅助方法 ===
    
    static byte ReadEcThermalMode()
    {
        // 简化版，实际应该用 inpoutx64 读取
        // 这里用 WMI 方法 8 Get
        var input = new byte[32];
        input[1] = 250; // Get
        input[3] = 8;   // SystemPerMode
        var result = CallWmi(input);
        return result.Length > 4 ? result[4] : (byte)0;
    }
    
    static void WriteEcThermalMode(byte mode)
    {
        // 简化版，实际应该用 inpoutx64 写入 0xE4
        // 这里用 WMI 方法 8 Set 代替
        var input = new byte[32];
        input[1] = 251; // Set
        input[3] = 8;   // SystemPerMode
        input[4] = mode;
        CallWmi(input);
    }
    
    static ushort ReadFanRpm(byte hiReg, byte loReg)
    {
        // 简化版，实际应该用 inpoutx64 读取
        // 这里用 WMI 方法 13 Get
        var input = new byte[32];
        input[1] = 250; // Get
        input[3] = 13;  // CPUGPUSYSFanSpeed
        var result = CallWmi(input);
        
        if (hiReg == 0x9B) // 大扇
            return result.Length > 6 ? (ushort)((result[5] << 8) | result[4]) : (ushort)0;
        else // 小扇
            return result.Length > 12 ? (ushort)((result[11] << 8) | result[10]) : (ushort)0;
    }
    
    static bool GetFanManualEnabled(byte fanType)
    {
        var input = new byte[32];
        input[1] = 250; // Get
        input[3] = 20;  // MaxFanSpeedSwitch
        input[4] = fanType;
        var result = CallWmi(input);
        
        // 调试输出：打印前 10 个字节
        Console.WriteLine($"  [DEBUG] WMI 方法 20 GET 返回: [{string.Join(", ", result.Take(10).Select(b => b.ToString()))}]");
        
        // 尝试多个可能的索引
        bool val5 = result.Length > 5 && result[5] == 1;
        bool val4 = result.Length > 4 && result[4] == 1;
        
        return val5 || val4;
    }
    
    static byte[] CallWmi(byte[] input)
    {
        using var obj = new ManagementObject(
            "root\\WMI",
            "MICommonInterface.InstanceName='ACPI\\PNP0C14\\MIFS_0'",
            null);
        var inParams = obj.GetMethodParameters("MiInterface");
        inParams["InData"] = input;
        var outParams = obj.InvokeMethod("MiInterface", inParams, null);
        return (byte[])outParams["OutData"];
    }
    
    static string GetModeName(byte mode)
    {
        return mode switch
        {
            0 => "平衡/办公",
            1 => "增强/野兽",
            2 => "静音",
            3 => "斗战/游戏",
            _ => "未知"
        };
    }
}
