using System;
using System.Text;

var dllPath = @"D:\Program Files\JiaoLong7.3\GamingControlCenter.KaronOC.dll";
var bytes = System.IO.File.ReadAllBytes(dllPath);
Console.WriteLine($"File: {dllPath}");
Console.WriteLine($"Size: {bytes.Length} bytes\n");

// PE header
var peOff = BitConverter.ToInt32(bytes, 0x3C);
var machine = BitConverter.ToUInt16(bytes, peOff + 4);
Console.WriteLine($"PE offset: 0x{peOff:X}");
Console.WriteLine($"Machine: 0x{machine:X} ({(machine == 0x8664 ? "x64" : machine == 0x14c ? "x86" : "?")})");

var numSections = BitConverter.ToUInt16(bytes, peOff + 6);
var optHdrSize = BitConverter.ToUInt16(bytes, peOff + 20);
var optOff = peOff + 24;
var magic = BitConverter.ToUInt16(bytes, optOff);
Console.WriteLine($"PE format: 0x{magic:X} ({(magic == 0x20b ? "PE32+" : "PE32")})");

// CLR header check
int clrDirOff = magic == 0x20b ? optOff + 232 : optOff + 216;
var clrRva = BitConverter.ToUInt32(bytes, clrDirOff);
Console.WriteLine($"CLR Header RVA: 0x{clrRva:X} (0 = native, non-.NET)");

// Exports
var exportDirOff = magic == 0x20b ? optOff + 112 : optOff + 96;
var exportRva = BitConverter.ToUInt32(bytes, exportDirOff);
var exportSize = BitConverter.ToUInt32(bytes, exportDirOff + 4);
Console.WriteLine($"\nExport Directory RVA: 0x{exportRva:X}, Size: {exportSize}");

// Parse sections to find RVA-to-file-offset mapping
Console.WriteLine($"\nSections ({numSections}):");
var sectionStart = optOff + optHdrSize;
var sections = new (string name, uint virtSize, uint virtAddr, uint rawSize, uint rawOff)[numSections];
for (int i = 0; i < numSections; i++)
{
    var off = sectionStart + i * 40;
    var name = Encoding.ASCII.GetString(bytes, off, 8).TrimEnd('\0');
    var vs = BitConverter.ToUInt32(bytes, off + 8);
    var va = BitConverter.ToUInt32(bytes, off + 12);
    var rs = BitConverter.ToUInt32(bytes, off + 16);
    var ro = BitConverter.ToUInt32(bytes, off + 20);
    sections[i] = (name, vs, va, rs, ro);
    Console.WriteLine($"  {name}: VA=0x{va:X} VS=0x{vs:X} RawOff=0x{ro:X} RawSz=0x{rs:X}");
}

uint RvaToFileOff(uint rva)
{
    foreach (var s in sections)
        if (rva >= s.virtAddr && rva < s.virtAddr + s.virtSize)
            return rva - s.virtAddr + s.rawOff;
    return 0;
}

if (exportRva != 0)
{
    var expOff = (int)RvaToFileOff(exportRva);
    Console.WriteLine($"\nExport table at file offset 0x{expOff:X}:");
    var nameRva = BitConverter.ToUInt32(bytes, expOff + 12);
    var numFuncs = BitConverter.ToInt32(bytes, expOff + 20);
    var numNames = BitConverter.ToInt32(bytes, expOff + 24);
    var addrTableRva = BitConverter.ToUInt32(bytes, expOff + 28);
    var namePtrRva = BitConverter.ToUInt32(bytes, expOff + 32);
    var ordinalTableRva = BitConverter.ToUInt32(bytes, expOff + 36);
    
    var dllNameOff = (int)RvaToFileOff(nameRva);
    var dllName = Encoding.ASCII.GetString(bytes, dllNameOff, 64).TrimEnd('\0');
    Console.WriteLine($"  DLL Name: {dllName}");
    Console.WriteLine($"  Functions: {numFuncs}, Names: {numNames}");
    
    var addrTableOff = (int)RvaToFileOff(addrTableRva);
    var namePtrOff = (int)RvaToFileOff(namePtrRva);
    var ordinalOff = (int)RvaToFileOff(ordinalTableRva);
    
    for (int i = 0; i < numNames; i++)
    {
        var fnNameRva = BitConverter.ToUInt32(bytes, namePtrOff + i * 4);
        var fnNameOff = (int)RvaToFileOff(fnNameRva);
        var fnName = Encoding.ASCII.GetString(bytes, fnNameOff, 128).TrimEnd('\0');
        var ordinal = BitConverter.ToUInt16(bytes, ordinalOff + i * 2);
        var funcRva = BitConverter.ToUInt32(bytes, addrTableOff + ordinal * 4);
        Console.WriteLine($"  [{ordinal}] {fnName} -> RVA 0x{funcRva:X}");
    }
}

// Also dump all readable ASCII strings > 4 chars that look like function/method names
Console.WriteLine("\n--- Interesting strings (method/function names) ---");
var sb = new StringBuilder();
for (int i = 0; i < bytes.Length; i++)
{
    var b = bytes[i];
    if (b >= 0x20 && b < 0x7F)
    {
        sb.Append((char)b);
    }
    else
    {
        if (sb.Length >= 6)
        {
            var s = sb.ToString();
            if (s.Contains("Pstate") || s.Contains("pstate") || s.Contains("NVAPI") || s.Contains("nvapi") ||
                s.Contains("nvapi64") || s.Contains("KaronOC") || s.Contains("GPU") || s.Contains("gpu") ||
                s.Contains("Overclock") || s.Contains("overclock") || s.Contains("Clock") || s.Contains("clock") ||
                s.Contains("Frequency") || s.Contains("frequency") || s.Contains("Offset") || s.Contains("offset") ||
                s.Contains("NvAPI") || s.Contains("GetP") || s.Contains("SetP") || s.Contains("Change") ||
                s.Contains("nvidia") || s.Contains("Dll") || s.Contains("Init") ||
                s.Contains("Power") || s.Contains("Thermal") || s.Contains("thermal"))
                Console.WriteLine($"  0x{i:X6}: {s}");
        }
        sb.Clear();
    }
}
