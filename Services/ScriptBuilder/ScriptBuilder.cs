using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace ProtoTestTool.Services.ScriptBuilder
{
    public class ScriptBuilder
    {
        private readonly ScriptCompiler _compiler = new();
        private readonly AssemblyContextManager _contextManager;
        private static readonly Regex GeneratedDllPattern = new(@".+\.[a-fA-F0-9]{8}\.dll$", RegexOptions.Compiled);

        public ScriptBuilder(AssemblyContextManager contextManager)
        {
            _contextManager = contextManager;
        }

        public async Task<Assembly?> CompileAsync(string workspacePath, Action<string, Brush> logAction)
        {
            if (string.IsNullOrEmpty(workspacePath)) return null;

            var scriptsDir = Path.Combine(workspacePath, BuildConstants.ScriptsFolder);
            if (!Directory.Exists(scriptsDir))
            {
                logAction($"Scripts folder not found: {scriptsDir}", Brushes.Red);
                return null;
            }

            try
            {
                logAction("Starting Compilation...", Brushes.White);

                // Collect Source Files
                var scriptFiles = Directory.GetFiles(scriptsDir, "*.cs", SearchOption.TopDirectoryOnly);
                if (scriptFiles.Length == 0)
                {
                    logAction("No .cs files found in Scripts folder.", Brushes.Orange);
                    return null;
                }

                logAction($"Found {scriptFiles.Length} script files.", Brushes.White);

                // 1. Unload Context
                _contextManager.Unload();

                // 2. Prepare Output Path
                var outputDll = Path.Combine(workspacePath, BuildConstants.ScriptDllName);
                if (File.Exists(outputDll))
                {
                    try
                    {
                        File.Delete(outputDll);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScriptBuilder] Failed to delete old DLL: {ex.Message}");
                        FileLogger.Instance.Error("Failed to delete old Script.dll", ex);
                    }
                }

                // 3. Collect References
                var refs = CollectReferences(workspacePath, scriptsDir);

                // 4. Compile
                await _compiler.CompileFilesToDllAsync(
                    scriptFiles, refs,
                    msg => logAction(msg, Brushes.Gray),
                    assemblyName: "Script",
                    outputPath: outputDll);

                logAction("Compilation Success! Loading Assembly...", Brushes.DeepSkyBlue);

                // 5. Load Assemblies (Proto & Script)
                
                // Load Proto first (if exists)
                var protosDll = Path.Combine(workspacePath, BuildConstants.ProtosDllName);
                if (File.Exists(protosDll))
                {
                    _contextManager.LoadProtoAssembly(protosDll);
                }

                // Load Script
                var assembly = _contextManager.LoadScriptAssembly(outputDll);
                return assembly;
            }
            catch (Exception ex)
            {
                logAction($"Error:\n{ex}", Brushes.Red);
                FileLogger.Instance.Error("ScriptBuilder.CompileAsync failed", ex);
                return null;
            }
        }

        private List<string> CollectReferences(string workspacePath, string scriptsDir)
        {
            var refs = new List<string>();
            var dlls = Directory.GetFiles(workspacePath, "*.dll").ToList();

            var libsDir = Path.Combine(scriptsDir, BuildConstants.LibsFolder);
            if (Directory.Exists(libsDir))
            {
                dlls.AddRange(Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories));
            }

            foreach (var dll in dlls)
            {
                var fileName = Path.GetFileName(dll);
                // Exclude Generated DLLs and Script.dll itself
                if (!GeneratedDllPattern.IsMatch(dll) &&
                    !fileName.Equals(BuildConstants.ScriptDllName, StringComparison.OrdinalIgnoreCase))
                {
                    refs.Add(dll);
                }
            }

            var protoGenDir = Path.Combine(workspacePath, BuildConstants.ProtoGenFolder);
            if (Directory.Exists(protoGenDir))
            {
                var protoDlls = Directory.GetFiles(protoGenDir, "*.dll", SearchOption.AllDirectories);
                foreach (var p in protoDlls)
                {
                    if (!refs.Contains(p) && !GeneratedDllPattern.IsMatch(p))
                    {
                        refs.Add(p);
                    }
                }
            }

            return refs;
        }
    }
}
