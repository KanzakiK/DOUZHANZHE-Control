using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

var jlDir = @"D:\Program Files\JiaoLong7.3";
var dllPath = Directory.GetFiles(jlDir, "*.dll").First(f => new FileInfo(f).Length > 20_000_000);
var fileBytes = File.ReadAllBytes(dllPath);

using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read);
using var pe = new PEReader(fs);
var mr = pe.GetMetadataReader();

// Full IL dump of all async state machines
var targets = new[] { "SetShowCPUMHz", "SetTuro", "SetCpuCore", "RecCpuCore", "SetCPUPerformanceStrategy",
    "CPU_Power_Consumption_Checked", "CPU_Power_Consumption_UnChecked",
    "CPU_Power_PL_Consumption_Checked", "CPU_Power_PL_Consumption_UnChecked",
    "CPUVoltage_ToggleButton_Checked", "CPUVoltage_ToggleButton_UnChecked",
    "Temperature_Wall_ToggleButton_Checked", "Temperature_Wall_ToggleButton_UnChecked" };

foreach (var th in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(th);
    var typeName = mr.GetString(td.Name);
    bool isTarget = targets.Any(t => typeName.Contains(t));
    if (!isTarget) continue;

    Console.WriteLine($"\n===== {typeName} =====");
    foreach (var mh in td.GetMethods())
    {
        var md = mr.GetMethodDefinition(mh);
        var methodName = mr.GetString(md.Name);
        if (methodName != "MoveNext" && methodName != ".ctor") continue;
        if (methodName == ".ctor") continue;

        Console.WriteLine($"\n--- {methodName} (full IL) ---");
        var rva = md.RelativeVirtualAddress;
        if (rva == 0) continue;
        try
        {
            var body = pe.GetMethodBody(rva);
            var il = body.GetILBytes();
            if (il == null) continue;
            FullDecode(il, mr);
        }
        catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); }
    }
}

// Also check MainWindow handlers that call ryzenadj/smu
Console.WriteLine("\n\n===== MainWindow <CPU_Power_Consumption_Checked>b__0 =====");
foreach (var th in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(th);
    var typeName = mr.GetString(td.Name);
    if (!typeName.Contains("DisplayClass204_0") && !typeName.Contains("DisplayClass205_0") &&
        !typeName.Contains("DisplayClass206_0") && !typeName.Contains("DisplayClass207_0") &&
        !typeName.Contains("DisplayClass210_0") && !typeName.Contains("DisplayClass211_0") &&
        !typeName.Contains("DisplayClass245_0") && !typeName.Contains("DisplayClass246_0"))
        continue;

    Console.WriteLine($"\n===== {typeName} =====");
    foreach (var mh in td.GetMethods())
    {
        var md = mr.GetMethodDefinition(mh);
        var methodName = mr.GetString(md.Name);
        if (methodName == ".ctor") continue;

        Console.WriteLine($"\n--- {methodName} ---");
        var rva = md.RelativeVirtualAddress;
        if (rva == 0) continue;
        try
        {
            var body = pe.GetMethodBody(rva);
            var il = body.GetILBytes();
            if (il == null) continue;
            FullDecode(il, mr);
        }
        catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); }
    }
}

static void FullDecode(byte[] il, MetadataReader mr)
{
    int pos = 0;
    while (pos < il.Length)
    {
        int start = pos;
        byte op = il[pos++];

        switch (op)
        {
            case 0x00: break; // nop
            case 0x02: case 0x03: case 0x04: case 0x05: // ldarg.0-3
            case 0x06: case 0x07: case 0x08: case 0x09: // ldloc.0-3
            case 0x0A: case 0x0B: case 0x0C: case 0x0D: break; // stloc.0-3
            case 0x14: break; // ldnull
            case 0x15: case 0x16: case 0x17: case 0x18: // ldc.i4.m1 to ldc.i4.2
            case 0x19: case 0x1A: case 0x1B: case 0x1C: case 0x1D: case 0x1E: break;
            case 0x1F: pos += 1; break; // ldc.i4.s
            case 0x20: pos += 4; break; // ldc.i4
            case 0x26: break; // dup
            case 0x27: break; // pop
            case 0x28: // call
            {
                int token = BitConverter.ToInt32(il, pos); pos += 4;
                var name = ResolveToken(token, mr);
                Console.WriteLine($"  {start:X4}: call {name}");
                break;
            }
            case 0x2A: break; // ret
            case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F: // branch.s
            case 0x30: case 0x31: case 0x32: pos += 1; break;
            case 0x38: case 0x39: case 0x3A: pos += 4; break; // branch
            case 0x58: case 0x59: case 0x5A: case 0x5B: break; // add/sub/mul/div
            case 0x5F: case 0x60: break; // and/or
            case 0x69: case 0x6B: case 0x6C: case 0x6D: break; // conv
            case 0x70: pos += 4; break; // box
            case 0x72: // ldstr
            {
                int token = BitConverter.ToInt32(il, pos); pos += 4;
                try {
                    var handle = System.Reflection.Metadata.Ecma335.MetadataTokens.UserStringHandle(token & 0x00FFFFFF);
                    Console.WriteLine($"  {start:X4}: ldstr \"{mr.GetUserString(handle)}\"");
                } catch { Console.WriteLine($"  {start:X4}: ldstr <0x{token:X8}>"); }
                break;
            }
            case 0x73: // newobj
            {
                int token = BitConverter.ToInt32(il, pos); pos += 4;
                Console.WriteLine($"  {start:X4}: newobj {ResolveToken(token, mr)}");
                break;
            }
            case 0x74: case 0x75: pos += 4; break; // castclass/isinst
            case 0x7B: // ldfld
            {
                int token = BitConverter.ToInt32(il, pos); pos += 4;
                Console.WriteLine($"  {start:X4}: ldfld {ResolveToken(token, mr)}");
                break;
            }
            case 0x7D: // stfld
            {
                int token = BitConverter.ToInt32(il, pos); pos += 4;
                Console.WriteLine($"  {start:X4}: stfld {ResolveToken(token, mr)}");
                break;
            }
            case 0x7E: // ldsfld
            {
                int token = BitConverter.ToInt32(il, pos); pos += 4;
                Console.WriteLine($"  {start:X4}: ldsfld {ResolveToken(token, mr)}");
                break;
            }
            case 0x7F: // stsfld
            {
                int token = BitConverter.ToInt32(il, pos); pos += 4;
                Console.WriteLine($"  {start:X4}: stsfld {ResolveToken(token, mr)}");
                break;
            }
            case 0x8C: case 0x8D: pos += 4; break; // unbox/newarr
            case 0x8F: break; // ldlen
            case 0x91: case 0x94: break; // ldelem
            case 0x9A: case 0x9E: break; // stelem
            case 0xA2: pos += 4; break;
            case 0xD0: pos += 4; break; // ldtoken
            case 0xDC: break; // endfinally
            case 0xDD: pos += 4; break; // leave
            case 0xDE: pos += 1; break; // leave.s
            case 0xFE:
            {
                byte op2 = il[pos++];
                if (op2 == 0x01 || op2 == 0x02 || op2 == 0x04) break; // ceq/cgt/clt
                if (op2 == 0x09 || op2 == 0x0B || op2 == 0x0C || op2 == 0x0E) pos += 2;
                break;
            }
            default: break;
        }
    }
}

static string ResolveToken(int token, MetadataReader mr)
{
    try
    {
        var handle = System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var md = mr.GetMethodDefinition((MethodDefinitionHandle)handle);
                var td = mr.GetTypeDefinition(md.GetDeclaringType());
                return $"{mr.GetString(td.Name)}.{mr.GetString(md.Name)}";
            }
            case HandleKind.MemberReference:
            {
                var mref = mr.GetMemberReference((MemberReferenceHandle)handle);
                var parent = mref.Parent;
                string pn = parent.Kind switch
                {
                    HandleKind.TypeReference => mr.GetString(mr.GetTypeReference((TypeReferenceHandle)parent).Name),
                    HandleKind.TypeDefinition => mr.GetString(mr.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                    _ => "?"
                };
                return $"{pn}.{mr.GetString(mref.Name)}";
            }
            case HandleKind.FieldDefinition:
            {
                var fd = mr.GetFieldDefinition((FieldDefinitionHandle)handle);
                var td = mr.GetTypeDefinition(fd.GetDeclaringType());
                return $"{mr.GetString(td.Name)}.{mr.GetString(fd.Name)}";
            }
            default: return $"0x{token:X8}";
        }
    }
    catch { return $"0x{token:X8}"; }
}
