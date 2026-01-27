using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool
{
    public class ScriptLoader
    {
        /// <summary>
        /// BCL DLL blacklist - these should NEVER be added from NuGet packages
        /// They must come from the runtime (TRUSTED_PLATFORM_ASSEMBLIES) only
        /// </summary>
        private static readonly HashSet<string> BclBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            // Core runtime
            "System.Private.CoreLib",
            "mscorlib",
            "netstandard",

            // System.* BCL assemblies that conflict with runtime
            "System.Runtime",
            "System.Runtime.Extensions",
            "System.Runtime.InteropServices",
            "System.Runtime.InteropServices.RuntimeInformation",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Memory",
            "System.Buffers",
            "System.Numerics.Vectors",
            "System.Collections",
            "System.Collections.Concurrent",
            "System.Collections.Immutable",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Text.Encoding",
            "System.Text.Encoding.Extensions",
            "System.Text.Json",
            "System.Text.Encodings.Web",
            "System.Text.RegularExpressions",
            "System.Threading",
            "System.Threading.Tasks",
            "System.Threading.Tasks.Extensions",
            "System.IO",
            "System.IO.Compression",
            "System.IO.FileSystem",
            "System.Console",
            "System.Diagnostics.Debug",
            "System.Diagnostics.Tracing",
            "System.ObjectModel",
            "System.Reflection",
            "System.Reflection.Emit",
            "System.Reflection.Emit.ILGeneration",
            "System.Reflection.Emit.Lightweight",
            "System.Reflection.Extensions",
            "System.Reflection.Metadata",
            "System.Reflection.Primitives",
            "System.Resources.ResourceManager",
            "System.ComponentModel",
            "System.ComponentModel.Primitives",
            "System.ComponentModel.TypeConverter",

            // Microsoft.* that should come from runtime
            "Microsoft.CSharp",
            "Microsoft.VisualBasic.Core",
            "Microsoft.Win32.Primitives",
            "Microsoft.Win32.Registry",
        };

        /// <summary>
        /// Check if a DLL name is a BCL assembly that should be excluded
        /// </summary>
        public static bool IsBclAssembly(string dllPath)
        {
            var fileName = Path.GetFileNameWithoutExtension(dllPath);

            // Check exact match in blacklist
            if (BclBlacklist.Contains(fileName))
                return true;

            // Check prefix patterns for broad System.*/Microsoft.* filtering
            // But allow specific packages like Google.Protobuf, Newtonsoft.Json, etc.
            if (fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) &&
                !IsAllowedSystemPackage(fileName))
                return true;

            if (fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
                !IsAllowedMicrosoftPackage(fileName))
                return true;

            return false;
        }

        /// <summary>
        /// System.* packages that ARE allowed (not part of BCL)
        /// </summary>
        private static bool IsAllowedSystemPackage(string name)
        {
            // These are actual NuGet packages, not BCL facades
            var allowed = new[]
            {
                "System.IO.Pipelines",           // Actual NuGet package
                "System.IO.Hashing",             // Actual NuGet package
                "System.Reactive",               // Rx
            };
            return allowed.Any(a => name.Equals(a, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Microsoft.* packages that ARE allowed (not part of BCL)
        /// </summary>
        private static bool IsAllowedMicrosoftPackage(string name)
        {
            var allowed = new[]
            {
                "Microsoft.IO.RecyclableMemoryStream",
                "Microsoft.Extensions.Logging",
                "Microsoft.Extensions.Logging.Abstractions",
                "Microsoft.Extensions.DependencyInjection",
                "Microsoft.Extensions.DependencyInjection.Abstractions",
                "Microsoft.Extensions.Options",
                "Microsoft.Extensions.Configuration",
                "Microsoft.Extensions.Configuration.Abstractions",
                "Microsoft.Extensions.Primitives",
                "Microsoft.Bcl.AsyncInterfaces",  // This one is tricky but sometimes needed
            };
            return allowed.Any(a => name.Equals(a, StringComparison.OrdinalIgnoreCase));
        }

        private CSharpCompilationOptions GetCompilationOptions()
        {
            return new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu);
        }

        /// <summary>
        /// Build references from TRUSTED_PLATFORM_ASSEMBLIES (runtime BCL)
        /// This ensures we use the actual runtime types, not NuGet facades
        /// </summary>
        private IEnumerable<MetadataReference> GetRuntimeReferences()
        {
            var refs = new List<MetadataReference>();
            var addedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Get trusted platform assemblies from runtime
            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrEmpty(trustedAssemblies))
            {
                var paths = trustedAssemblies.Split(Path.PathSeparator);
                foreach (var path in paths)
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        continue;

                    var name = Path.GetFileNameWithoutExtension(path);
                    if (addedAssemblies.Contains(name))
                        continue;

                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(path));
                        addedAssemblies.Add(name);
                    }
                    catch
                    {
                        // Skip problematic assemblies
                    }
                }
            }

            return refs;
        }

        /// <summary>
        /// Get application-specific references (non-BCL)
        /// </summary>
        private IEnumerable<MetadataReference> GetApplicationReferences()
        {
            var refs = new List<MetadataReference>();
            var addedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add Google.Protobuf
            AddIfNotExists(refs, addedAssemblies, typeof(Google.Protobuf.IMessage).Assembly.Location);

            // Add ScriptContract (ProtoTestTool types)
            AddIfNotExists(refs, addedAssemblies, typeof(ScriptGlobals).Assembly.Location);

            return refs;
        }

        private void AddIfNotExists(List<MetadataReference> refs, HashSet<string> added, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            var name = Path.GetFileNameWithoutExtension(path);
            if (added.Contains(name))
                return;

            refs.Add(MetadataReference.CreateFromFile(path));
            added.Add(name);
        }

        /// <summary>
        /// Filter external references to exclude BCL assemblies
        /// </summary>
        public IEnumerable<string> FilterReferences(IEnumerable<string> referencePaths)
        {
            foreach (var path in referencePaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;

                if (IsBclAssembly(path))
                {
                    // Log filtered BCL for debugging
                    System.Diagnostics.Debug.WriteLine($"[ScriptLoader] Filtered BCL: {Path.GetFileName(path)}");
                    continue;
                }

                yield return path;
            }
        }

        public async Task<string> CompileFilesToDllAsync(
            IEnumerable<string> sourceFiles,
            IEnumerable<string>? referencePaths = null,
            Action<string>? logger = null,
            string? assemblyName = null,
            string? outputPath = null)
        {
            var sourceFileStart = sourceFiles.FirstOrDefault();
            var dir = sourceFileStart != null ? Path.GetDirectoryName(sourceFileStart) : AppDomain.CurrentDomain.BaseDirectory;

            // Use provided assemblyName or generate random one
            var finalAssemblyName = assemblyName ?? $"ScriptBundle_{Guid.NewGuid():N}";

            // Use provided outputPath or generate in source directory
            var dllPath = outputPath ?? Path.Combine(dir ?? "", $"{finalAssemblyName}.dll");
            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");

            // Cleanup old ScriptBundle files (legacy)
            try
            {
                var oldFiles = Directory.GetFiles(dir ?? "", "ScriptBundle.*.dll")
                    .Concat(Directory.GetFiles(dir ?? "", "ScriptBundle.*.pdb"));
                foreach (var oldFile in oldFiles)
                {
                    try { File.Delete(oldFile); } catch { }
                }
            }
            catch { }

            // Prepare Syntax Trees
            var syntaxTrees = new List<SyntaxTree>();
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular);

            foreach (var file in sourceFiles)
            {
                if (!File.Exists(file)) continue;
                var text = await File.ReadAllTextAsync(file);
                var sourceText = SourceText.From(text, Encoding.UTF8);
                var tree = CSharpSyntaxTree.ParseText(sourceText, parseOptions, file);
                syntaxTrees.Add(tree);
            }

            // Build references: Runtime BCL + Application + Filtered External
            var references = new List<MetadataReference>();
            var addedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Add runtime BCL references (from TRUSTED_PLATFORM_ASSEMBLIES)
            foreach (var r in GetRuntimeReferences())
            {
                var name = Path.GetFileNameWithoutExtension(r.Display ?? "");
                if (!addedAssemblies.Contains(name))
                {
                    references.Add(r);
                    addedAssemblies.Add(name);
                }
            }
            logger?.Invoke($"Added {addedAssemblies.Count} runtime references.");

            // 2. Add application references
            foreach (var r in GetApplicationReferences())
            {
                var name = Path.GetFileNameWithoutExtension(r.Display ?? "");
                if (!addedAssemblies.Contains(name))
                {
                    references.Add(r);
                    addedAssemblies.Add(name);
                }
            }

            // 3. Add external references (filtered to exclude BCL)
            if (referencePaths != null)
            {
                var filtered = FilterReferences(referencePaths).ToList();
                var addedExternal = 0;

                foreach (var refPath in filtered)
                {
                    var name = Path.GetFileNameWithoutExtension(refPath);
                    if (addedAssemblies.Contains(name))
                        continue;

                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(refPath));
                        addedAssemblies.Add(name);
                        addedExternal++;
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"Warning: Could not add reference {name}: {ex.Message}");
                    }
                }

                logger?.Invoke($"Added {addedExternal} external references.");
            }

            // Compile
            var compilation = CSharpCompilation.Create(
                finalAssemblyName,
                syntaxTrees,
                references,
                GetCompilationOptions()
            );

            using var peStream = File.Create(dllPath);
            using var pdbStream = File.Create(pdbPath);

            var emitResult = compilation.Emit(peStream, pdbStream);

            if (!emitResult.Success)
            {
                var errors = string.Join(Environment.NewLine, emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d =>
                    {
                        var loc = d.Location.GetLineSpan();
                        var fileName = Path.GetFileName(loc.Path);
                        return $"[{fileName}] Line {loc.StartLinePosition.Line + 1}: {d.GetMessage()}";
                    }));

                throw new InvalidOperationException($"Compilation failed:{Environment.NewLine}{errors}");
            }

            logger?.Invoke("Compilation successful.");
            return dllPath;
        }
    }
}
