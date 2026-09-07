using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;

class Program
{
    static void Main(string[] args)
    {
        string inputPath = @"C:\Users\USER\.nuget\packages\lib.harmony\2.3.3\lib\net8.0\0Harmony.dll";
        string outputPath = @"C:\Users\USER\Desktop\mynote\Yes24PDFDumper_AGY\LibCore.dll";

        var asm = AssemblyDefinition.ReadAssembly(inputPath);
        asm.Name.Name = "LibCore";
        asm.MainModule.Name = "LibCore.dll";
        asm.Write(outputPath);

        var loaded = Assembly.LoadFrom(outputPath);
        Console.WriteLine($"Loaded Assembly FullName: {loaded.FullName}");
        Console.WriteLine($"Does FullName contain 'Harmony'?: {loaded.FullName.Contains("Harmony")}");
        Console.WriteLine($"Does FullName contain 'Dumper'?: {loaded.FullName.Contains("Dumper")}");
    }
}
