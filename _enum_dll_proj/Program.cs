
using System;
using System.IO;
using System.Reflection;
using System.Linq;

var dllPath = @"C:\Program Files (x86)\斗战者控制台\斗战者控制台.dll";
AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
{
    var n = new AssemblyName(args.Name).Name;
    if (n.StartsWith("System") || n.StartsWith("Microsoft.")) return null;
    var p = Path.Combine(Path.GetDirectoryName(dllPath), n + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

var asm = Assembly.LoadFrom(dllPath);
Type enm = null;
foreach (var t in asm.GetTypes())
    if (t.Name == "WMIMethodName" && t.IsEnum) { enm = t; break; }

if (enm == null) { Console.Error.WriteLine("WMIMethodName not found"); return; }

var names = Enum.GetNames(enm);
foreach (var n in names.OrderBy(x => x))
{
    var v = (int)Enum.Parse(enm, n);
    var isFan = n.Contains("fan", StringComparison.OrdinalIgnoreCase)
             || n.Contains("speed", StringComparison.OrdinalIgnoreCase)
             || n.Contains("CPUGPU", StringComparison.OrdinalIgnoreCase);
    if (isFan)
        Console.WriteLine($"FAN>> {n} = {v}");
}
Console.WriteLine("=== ALL ENUMS ===");
foreach (var n in names.OrderBy(x => x))
    Console.WriteLine(n);
