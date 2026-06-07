using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

// Load the managed wrapper DLL
var asm = Assembly.LoadFrom(@"D:\Program Files\JiaoLong7.3\GamingControlCenter.KaronOC.dll");
Console.WriteLine($"Assembly: {asm.FullName}\n");

foreach (var t in asm.GetTypes())
{
    Console.WriteLine($"=== Type: {t.FullName} (Base: {t.BaseType?.Name}) ===");
    
    // Fields
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  Field: {f.FieldType.Name} {f.Name} [{(f.IsPublic?"pub":"priv")}{(f.IsStatic?" static":"")}]");
    
    // Properties
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  Prop: {p.PropertyType.Name} {p.Name}");

    // Methods (skip inherited Object methods)
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        .Where(m => m.DeclaringType == t))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  Method: {m.ReturnType.Name} {m.Name}({parms})");
        
        // Check for DllImport
        var dllAttr = m.GetCustomAttribute<DllImportAttribute>();
        if (dllAttr != null)
            Console.WriteLine($"    [DllImport(\"{dllAttr.Value}\")] CallingConvention={dllAttr.CallingConvention}");
    }
    Console.WriteLine();
}
