using System;
using System.Linq;
using System.Management;

var scope = new ManagementScope(@"\\localhost\root\wmi");
scope.Connect();
var searcher = new ManagementObjectSearcher(scope, new SelectQuery("MICommonInterface"));
var obj = searcher.Get().Cast<ManagementObject>().First();

var input = new byte[32];
input[1] = 250; // Get
input[3] = 9;   // GPUMode
var inParams = obj.GetMethodParameters("MiInterface");
inParams["InData"] = input;
var outParams = obj.InvokeMethod("MiInterface", inParams, null);
var result = (byte[])outParams["OutData"];
Console.WriteLine($"GPU mode raw byte[4] = {result[4]}");
Console.WriteLine($"Full output bytes (0-15): {string.Join(" ", result.Take(16))}");
