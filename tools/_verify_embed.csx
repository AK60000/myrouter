#r "System.Reflection"
using System;
using System.IO;
using System.Linq;
using System.Reflection;

var dllPath = Args.Count > 0
    ? Args[0]
    : @"C:\code\C#\myrouter\bin\Release\net10.0-windows\myrouter.dll";
var icoPath = Args.Count > 1
    ? Args[1]
    : @"C:\code\C#\myrouter\myrouter.ico";

var asm = Assembly.LoadFile(dllPath);
var names = asm.GetManifestResourceNames();
Console.WriteLine($"manifest resources ({names.Length}):");
foreach (var n in names) Console.WriteLine($"  {n}");

var expected = File.ReadAllBytes(icoPath);
var icoName = names.FirstOrDefault(n => n.EndsWith("myrouter.ico", StringComparison.OrdinalIgnoreCase));
if (icoName == null)
{
    Console.WriteLine("!! myrouter.ico resource not found");
    return;
}
var stream = asm.GetManifestResourceStream(icoName);
var ms = new MemoryStream();
stream.CopyTo(ms);
var actual = ms.ToArray();
Console.WriteLine($"ico resource: {icoName}  bytes={actual.Length}  expected={expected.Length}");
Console.WriteLine(actual.SequenceEqual(expected) ? "OK: embedded ico MATCHES the new icon (tray + title bar will use it)" : "!! MISMATCH — stale resource");
