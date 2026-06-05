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
        var inParams = obj.GetMethodParameters("MiInterface");
        inParams["InData"] = input;
        var outParams = obj.InvokeMethod("MiInterface", inParams, null);
        return (byte[])outParams["OutData"];
    }

    // ---- GPUMode (?? 9) ----
    public byte GetGpuMode()
    {
        var input = new byte[32];
        input[1] = 250; // Get
        input[3] = 9;   // GPUMode
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)0;
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
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- FnLock (?? 11) ----
    public byte GetFnLock()
    {
        var input = new byte[32];
        input[1] = 250;
        input[3] = 11;
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)0;
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
            return true;
        }
        catch { return false; }
    }

    // ---- TPLock / TouchpadLock (?? 12) ----
    public byte GetTouchpadLock()
    {
        var input = new byte[32];
        input[1] = 250;
        input[3] = 12;
        var result = CallMethod(input);
        return result.Length > 4 ? result[4] : (byte)0;
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
            return true;
        }
        catch { return false; }
    }

    // ---- Fan control (Bellator protocol: data[4]=FanType, data[5]=value) ----
    // FanType: 0=CPUGPUFan(大扇), 1=SYSFan(小扇)
    public bool SetFanManual(bool enable)
    {
        try
        {
            // MaxFanSwitch(20): data[4]=FanType(0), data[5]=enable
            var input = new byte[32];
            input[1] = 251;
            input[3] = 20;
            input[4] = 0;   // CPUGPUFan (大扇)
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
