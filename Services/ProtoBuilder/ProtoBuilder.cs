using System.IO;
using System.Reflection;
using System.Windows.Media;
using ProtoTestTool.Network;

namespace ProtoTestTool.Services.ProtoBuilder
{
    public class ProtoBuilder
    {
        private readonly ProtoCompiler _compiler = new();
        private readonly ScriptBuilder.ScriptCompiler _scriptCompiler = new(); // Use ScriptCompiler to build Protos.dll
        private readonly AssemblyContextManager _contextManager;

        public ProtoBuilder(AssemblyContextManager contextManager)
        {
            _contextManager = contextManager;
        }

        public async Task<Assembly?> ProcessProtosAsync(string[] protoFiles, string workspacePath, Action<string> logAction, Action<string, Brush> appendLog)
        {
            try
            {
                logAction($"\n[Manager] Processing {protoFiles.Length} files...");
                appendLog($"[Proto] Processing {protoFiles.Length} files...", Brushes.MediumPurple);

                var targetDir = !string.IsNullOrEmpty(workspacePath)
                    ? Path.Combine(workspacePath, BuildConstants.ProtosFolder, BuildConstants.ProtoGenFolder)
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BuildConstants.ProtosFolder, BuildConstants.ProtoGenFolder);

                // Force GC to help unload (ContextManager handles unload logic, but explicit GC here reinforces it if called externally)
                GC.Collect();
                GC.WaitForPendingFinalizers();

                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                // Clean old CS files
                CleanOldFiles(targetDir, "*.cs", logAction);

                // Compile Proto -> CS
                try
                {
                    logAction($"\n  - Compiling {protoFiles.Length} proto files...");
                    _compiler.CompileProtosToCSharp(protoFiles, targetDir);
                    logAction($"\n  - Proto to C# conversion complete.");
                }
                catch (Exception ex)
                {
                    logAction($"\n[Error] Proto compilation failed: {ex.Message}");
                    appendLog($"[Proto Error] {ex.Message}", Brushes.Red);
                    throw;
                }

                // Compile CS -> Protos.dll
                var csFiles = Directory.GetFiles(targetDir, "*.cs");
                if (csFiles.Length > 0)
                {
                    logAction($"\n[Manager] Building Protos.dll from {csFiles.Length} sources...");

                    var outputDll = Path.Combine(!string.IsNullOrEmpty(workspacePath) ? workspacePath : targetDir, BuildConstants.ProtosDllName);

                    // Unload previous context via Manager
                    _contextManager.Unload();
                    logAction($"\n  - Previous assemblies unloaded.");

                    // Delete old DLL
                    if (File.Exists(outputDll))
                    {
                        try
                        {
                            File.Delete(outputDll);
                        }
                        catch (Exception ex)
                        {
                            logAction($"\n[Proto] Failed to delete old dll: {ex.Message}");
                        }
                    }

                    // Compile to DLL using ScriptCompiler (reused for CS -> DLL)
                    await _scriptCompiler.CompileFilesToDllAsync(
                        csFiles, null,
                        msg => logAction($"\n  {msg}"),
                        assemblyName: "Protos",
                        outputPath: outputDll);

                    // Load Protos.dll
                    var assembly = _contextManager.LoadProtoAssembly(outputDll);
                    return assembly;
                }
                else
                {
                    logAction($"\n[Manager] No C# files generated.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logAction($"\n[Critical Error] {ex.Message}");
                appendLog($"[Proto Error] {ex.Message}", Brushes.Red);
                throw;
            }
        }

        private void CleanOldFiles(string dir, string pattern, Action<string> log)
        {
            var files = Directory.GetFiles(dir, pattern);
            foreach (var f in files)
            {
                try
                {
                    File.Delete(f);
                }
                catch (Exception ex)
                {
                    log($"\n[Proto] Failed to delete {Path.GetFileName(f)}: {ex.Message}");
                }
            }
        }
    }
}