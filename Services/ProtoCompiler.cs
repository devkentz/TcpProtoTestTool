using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ProtoTestTool.Services
{
    public class ProtoCompiler
    {
        public static string GetProtocPath()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var protocPath = Path.Combine(basePath, "protoc.exe");
            if (File.Exists(protocPath)) return protocPath;
            throw new FileNotFoundException($"protoc.exe not found at {protocPath}. Ensure project build copies it.");
        }

        /// <summary>
        /// Compile a single .proto file to .cs output
        /// </summary>
        public void CompileProtoToCSharp(string protoPath, string outputDir)
        {
            CompileProtosToCSharp([protoPath], outputDir);
        }

        /// <summary>
        /// Compile multiple .proto files to .cs output (handles imports correctly)
        /// </summary>
        public void CompileProtosToCSharp(string[] protoPaths, string outputDir)
        {
            if (protoPaths.Length == 0) return;

            Directory.CreateDirectory(outputDir);

            var protoc = GetProtocPath();

            // Collect all unique directories for -I include paths
            var includeDirs = protoPaths
                .Select(p => Path.GetDirectoryName(p)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var includeArgs = string.Join(" ", includeDirs.Select(d => $"-I=\"{d}\""));
            var protoFilesArgs = string.Join(" ", protoPaths.Select(p => $"\"{p}\""));
            var args = $"{includeArgs} --csharp_out=\"{outputDir}\" {protoFilesArgs}";

            var psi = new ProcessStartInfo
            {
                FileName = protoc,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc!.WaitForExit();

            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new Exception($"Protoc failed:\n{err}");
            }
        }

        public static Assembly? Compile(string protoDir)
        {
            if (!Directory.Exists(protoDir)) return null;

            var protos = Directory.GetFiles(protoDir, "*.proto");
            if (!protos.Any()) return null;

            var outputDir = Path.Combine(protoDir, "Generated");
            Directory.CreateDirectory(outputDir);

            // Clean previous generated files to avoid stale data
            foreach (var file in Directory.GetFiles(outputDir, "*.cs"))
            {
                try { File.Delete(file); } catch { }
            }

            var protoc = GetProtocPath();
            var protoFilesArgs = string.Join(" ", protos.Select(p => $"\"{p}\""));
            // -I must point to protoDir to resolve imports
            var args = $"-I=\"{protoDir}\" --csharp_out=\"{outputDir}\" {protoFilesArgs}";

            var psi = new ProcessStartInfo
            {
                FileName = protoc,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc.WaitForExit();
            
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new Exception($"Protoc compilation failed:\n{err}");
            }

            // Compile C#
            var csFiles = Directory.GetFiles(outputDir, "*.cs");
            if (!csFiles.Any())
            {
                // Might happen if proto files are empty or don't generate messages
                return null;
            }

            var syntaxTrees = csFiles.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)));

            var references = new List<MetadataReference>();
            // Add all loaded assemblies as references to ensure types are available
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
                {
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
                }
            }

            var compilation = CSharpCompilation.Create(
                $"Protos_{Guid.NewGuid():N}",
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);

            if (!result.Success)
            {
                var failures = result.Diagnostics.Where(d => d.IsWarningAsError || d.Severity == DiagnosticSeverity.Error);
                var errorMsg = string.Join("\n", failures.Select(d => $"{d.Id}: {d.GetMessage()}"));
                throw new Exception($"C# Compilation failed:\n{errorMsg}");
            }

            ms.Seek(0, SeekOrigin.Begin);
            return Assembly.Load(ms.ToArray());
        }
    }
}
