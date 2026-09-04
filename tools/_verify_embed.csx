#r "System.Reflection"
using System;
using System.IO;
using System.Linq;
using System.Reflection;

var dllPath = Args.Count > 0
    ? Args[0]
    : @"C:\code\C#\myrouter\bin\Release\net10.0-windows\myrouter.dll";
var cmpPath = Args.Count > 1
    ? Args[1]
    : @"C:\code\C#\myrouter\myrouter.ico";

var asm = Assembly.LoadFile(dllPath);
var names = asm.GetManifestResourceNames();
Console.WriteLine($"manifest resources ({names.Length}):");
foreach (var n in names) Console.WriteLine($"  {n}");

var suffix = Path.GetFileName(cmpPath);
var expected = File.ReadAllBytes(cmpPath);
var resName = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
if (resName == null)
{
    Console.WriteLine($"!! {suffix} resource not found");
    return;
}
var stream = asm.GetManifestResourceStream(resName);
var ms = new MemoryStream();
stream.CopyTo(ms);
var actual = ms.ToArray();
Console.WriteLine($"{suffix} resource: {resName}  bytes={actual.Length}  expected={expected.Length}");
Console.WriteLine(actual.SequenceEqual(expected) ? $"OK: embedded {suffix} MATCHES source" : $"!! MISMATCH — stale {suffix} resource");
