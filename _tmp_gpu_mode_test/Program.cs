using System;
using System.Management;

class Program
{
    static byte GetGpuMode()
    {
        var input = new byte[32];
        input[1] = 250; // Get
        input[3] = 9;   // GPUMode
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)255;
    }

    static byte[] CallMethod(byte[] input)
    {
        var scope = new ManagementScope(@"root\WMI");
        scope.Connect();
        var query = new SelectQuery("MICommonInterface");
        var searcher = new ManagementObjectSearcher(scope, query);
        foreach (ManagementObject obj in searcher.Get())
        {
            var inParams = obj.GetMethodParameters("MiInterface");
            inParams["InData"] = input;
            var outParams = obj.InvokeMethod("MiInterface", inParams, null);
            return (byte[])outParams["OutData"];
        }
        throw new Exception("MICommonInterface not found");
    }

    static void Main()
    {
        try
        {
            byte mode = GetGpuMode();
            Console.WriteLine($"WMI GetGpuMode() = {mode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
