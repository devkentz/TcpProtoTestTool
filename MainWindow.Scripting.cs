using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Collections.ObjectModel;
using ProtoTestTool.Controls;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;
using ProtoTestTool.Services;

namespace ProtoTestTool
{
    public partial class MainWindow
    {
        private ProxyServer? _proxyServer;
        private ProxyInterceptorPipeline? _proxyPipeline; // Hot Reload Support
        private IScriptStateStore? _scriptState;

        private static readonly Regex GeneratedDllPattern = new(@".+\.[a-fA-F0-9]{8}\.dll$", RegexOptions.Compiled);

        // Single unloadable context for both Proto and Script assemblies
        private UnloadableAssemblyContext? _workspaceAssemblyContext;
        private Assembly? _protoAssembly;
        private Assembly? _scriptAssembly;

        // Document IDs for Reference Updates
        public async Task CompileScriptsAsync(string workspacePath, Action<string, Brush> logAction)
        {
            if (string.IsNullOrEmpty(workspacePath)) return;

            var scriptsDir = Path.Combine(workspacePath, "Scripts");
            if (!Directory.Exists(scriptsDir))
            {
                logAction($"Scripts folder not found: {scriptsDir}", Brushes.Red);
                return;
            }

            try
            {
                logAction("Starting Compilation...", Brushes.White);

                // Cleanup legacy build files
                var legacyBuildFile = Path.Combine(scriptsDir, "PacketInterceptor.Build.cs");
                if (File.Exists(legacyBuildFile)) File.Delete(legacyBuildFile);

                // Collect Source Files
                var scriptFiles = Directory.GetFiles(scriptsDir, "*.cs", SearchOption.TopDirectoryOnly);
                if (scriptFiles.Length == 0)
                {
                    logAction("No .cs files found in Scripts folder.", Brushes.Orange);
                    return;
                }

                logAction($"Found {scriptFiles.Length} script files.", Brushes.White);

                UnloadPreviousAssembly();

                var outputDll = Path.Combine(workspacePath, "Script.dll");
                if (File.Exists(outputDll))
                {
                    try
                    {
                        File.Delete(outputDll);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScriptLoader] Failed to delete old DLL: {ex.Message}");
                    }
                }

                var refs = CollectReferences(workspacePath, scriptsDir);

                await _scriptLoader.CompileFilesToDllAsync(
                    scriptFiles, refs,
                    msg => logAction(msg, Brushes.Gray),
                    assemblyName: "Script",
                    outputPath: outputDll);

                logAction("Compilation Success! Loading Assembly...", Brushes.DeepSkyBlue);

                var assembly = LoadAssemblies(workspacePath, outputDll);


                var registryType = assembly.GetTypes()
                    .FirstOrDefault(t => typeof(IPacketRegistry).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                IPacketRegistry registry;
                if (registryType != null)
                {
                    registry = (IPacketRegistry) Activator.CreateInstance(registryType)!;
                    logAction($"[Registry] Using script-defined {registryType.Name}", Brushes.DeepSkyBlue);
                }
                else
                {
                    registry = ProtoLoaderManager.Instance;
                }

                var codecType = assembly.GetTypes().FirstOrDefault(t => typeof(IPacketCodec).IsAssignableFrom(t) && t is {IsAbstract: false, IsInterface: false});
                
                if (codecType == null) 
                    throw new Exception("IPacketCodec implementation not found in scripts.");
                
                var codec = (IPacketCodec) Activator.CreateInstance(codecType)!;

                InitializeScriptGlobals(registry, codec);
                UpdateInterceptors(assembly, logAction);

                PacketSelectorControl.Refresh();
                await Dispatcher.InvokeAsync(() => _ = LoadHeaderJsonAsync());

                UpdateIntellisense(assembly, logAction);
            }
            catch (Exception ex)
            {
                logAction($"Error:\n{ex}", Brushes.Red);
            }
        }

        private void UnloadPreviousAssembly()
        {
            _proxyPipeline?.Clear();

            if (_workspaceAssemblyContext != null)
            {
                ProtoLoaderManager.Instance.Clear();
                _scriptAssembly = null;
                _protoAssembly = null;
                _workspaceAssemblyContext.Unload();
                _workspaceAssemblyContext = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private List<string> CollectReferences(string workspacePath, string scriptsDir)
        {
            var refs = new List<string>();
            var dlls = Directory.GetFiles(workspacePath, "*.dll").ToList();

            var libsDir = Path.Combine(scriptsDir, "Libs");
            if (Directory.Exists(libsDir))
            {
                dlls.AddRange(Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories));
            }

            foreach (var dll in dlls)
            {
                var fileName = Path.GetFileName(dll);
                if (!GeneratedDllPattern.IsMatch(dll) &&
                    !fileName.Equals("Script.dll", StringComparison.OrdinalIgnoreCase))
                {
                    refs.Add(dll);
                }
            }

            var protoGenDir = Path.Combine(workspacePath, "ProtoGen");
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

        private Assembly LoadAssemblies(string workspacePath, string outputDll)
        {
            _workspaceAssemblyContext = new UnloadableAssemblyContext();

            var protosDll = Path.Combine(workspacePath, "Protos.dll");
            if (File.Exists(protosDll))
            {
                _protoAssembly = _workspaceAssemblyContext.LoadFromFile(protosDll);

                var messageTypes = _protoAssembly.GetTypes()
                    .Where(t => typeof(Google.Protobuf.IMessage).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var type in messageTypes)
                    ProtoLoaderManager.Instance.RegisterPacket(type);
            }

            var assembly = _workspaceAssemblyContext.LoadFromFile(outputDll);
            _scriptAssembly = assembly;
            return assembly;
        }

        private void InitializeScriptGlobals(IPacketRegistry registry, IPacketCodec codec)
        {
            _scriptState ??= new ScriptStateStore();

            var toolLogger = new ToolScriptLogger((msg, color) => { Dispatcher.Invoke(() => AppendLog(msg, color)); });

            ScriptGlobals.Initialize(_scriptState, toolLogger);
            ScriptGlobals.SetServices(registry, codec);
        }

        private void UpdateInterceptors(Assembly assembly, Action<string, Brush> logAction)
        {
            var interceptorTypes = assembly.GetTypes()
                .Where(t => typeof(IPacketInterceptor).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .Select(t => new InterceptorItem(t.Name, t))
                .OrderBy(n => n.Name)
                .ToList();

            Dispatcher.Invoke(() =>
            {
                var config = _currentConfig ?? new WorkspaceConfig();

                // Helper to setup selector
                void SetupSelector(InterceptorSelector selector, string key)
                {
                    var active = config.ActiveInterceptors.GetValueOrDefault(key, []);
                    selector.SetInterceptors(interceptorTypes, active);

                    // Hook event to save on change
                    selector.SelectionChanged -= Selector_SelectionChanged;
                    selector.SelectionChanged += Selector_SelectionChanged;
                }

                SetupSelector(ClientInterceptorSelector, "Client");
                SetupSelector(ProxyInterceptorSelector, "Proxy");
                SetupSelector(ReplayInterceptorSelector, "Replay");
            });

            logAction($"[Interceptors] Found {interceptorTypes.Count} interceptors.", Brushes.LimeGreen);
        }

        private void Selector_SelectionChanged(object sender, RoutedEventArgs e)
        {
            SaveWorkspaceConfiguration();
        }

        private void UpdateIntellisense(Assembly assembly, Action<string, Brush> logAction)
        {
            var types = new List<Type>
            {
                typeof(ScriptGlobals),
                typeof(IScriptStateStore),
                typeof(IScriptLogger),
            };

            if (ScriptGlobals.Registry != null)
                types.AddRange(ScriptGlobals.Registry.GetMessageTypes());

            types.AddRange(assembly.GetTypes().Where(t => t.IsPublic));

            var json = CompletionService.GenerateCompletionJson(types);
            logAction("[Intellisense] Updated metadata.", Brushes.Gray);

            Dispatcher.Invoke(() =>
            {
                if (_scriptEditorWindow != null && _scriptEditorWindow.IsLoaded)
                {
                    _ = _scriptEditorWindow.UpdateCompletionsAsync(json);
                }
            });
        }

        // ...

        private Task StartProxyServerAsync(int localPort, string targetIp, int targetPort)
        {
            return Task.Run(() =>
            {
                try
                {
                    var assembly = _scriptAssembly;
                    if (assembly == null) 
                        throw new Exception("Script assembly not loaded.");

                    // 1. Get Codec from Globals
                    if (ScriptGlobals.Codec == null) 
                        throw new Exception("IPacketCodec이 초기화되지 않았습니다. (Compile First)");
                    
                    var codec = ScriptGlobals.Codec;

                    // 2. Find Interceptors
                    _proxyPipeline = new ProxyInterceptorPipeline(); // Assign to field

                    // Get Active Interceptors from UI (Dispatcher)
                    List<InterceptorItem> activeInterceptorNames = new();
                    Dispatcher.Invoke(() => { activeInterceptorNames = ProxyInterceptorSelector.GetActiveInterceptors(); });

                    foreach (var interceptorItem in activeInterceptorNames)
                    {
                        var interceptor = (IPacketInterceptor) Activator.CreateInstance(interceptorItem.Type)!;
                        _proxyPipeline.Add(interceptor);
                    }

                    // 3. Create Server
                    _proxyServer = new ProxyServer("0.0.0.0", localPort, targetIp, targetPort, _proxyPipeline, codec);
                    _proxyServer.Start();

                    Dispatcher.Invoke(() => AppendProxyLog($"Proxy Started on {localPort} -> {targetIp}:{targetPort}"));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendProxyLog($"Error starting proxy: {ex.Message}"));
                    throw;
                }
            });
        }


        public void InitializeWorkspaceFiles(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return;

            var scriptsDir = Path.Combine(workspacePath, "Scripts");
            if (!Directory.Exists(scriptsDir)) Directory.CreateDirectory(scriptsDir);

            // Create default .cs files
            CreateIfMissing(scriptsDir, "PacketHeader.cs", "PacketHeader");
            CreateIfMissing(scriptsDir, "PacketCodec.cs", "PacketCodec");
            CreateIfMissing(scriptsDir, "PacketRegistry.cs", "PacketRegistry");
            CreateIfMissing(scriptsDir, "PacketInterceptor.cs", "PacketInterceptor");

            var configPath = Path.Combine(workspacePath, "workspace_config.json");
            if (!File.Exists(configPath))
            {
                var defaultConfig = new WorkspaceConfig();
                defaultConfig.Save(workspacePath);
                Dispatcher.Invoke(() => AppendLog($"[Workspace] Created workspace_config.json", Brushes.Green));
            }
        }


        private void CreateIfMissing(string dir, string fileName, string templateName)
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
            {
                try
                {
                    File.WriteAllText(path, ScriptTemplateFactory.GetTemplate(templateName));
                    Dispatcher.Invoke(() => AppendLog($"[Workspace] Created {fileName}", Brushes.Green));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"[Error] Failed to create {fileName}: {ex.Message}", Brushes.Red));
                }
            }
        }


        #region Proto Manager

        private readonly ObservableCollection<string> _loadedProtoFiles = new ObservableCollection<string>();
        // Note: _loadedMessageTypes would normally be derived from registry, 
        // but here we can track what we just imported.

        private void ReloadProtoBtn_Click(object sender, RoutedEventArgs e) => _ = ReloadProtoBtn_ClickAsync();

        private async Task ReloadProtoBtn_ClickAsync()
        {
            try
            {
                var protoDir = !string.IsNullOrEmpty(_workspacePath)
                    ? Path.Combine(_workspacePath, "Protos")
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Protos");

                if (!Directory.Exists(protoDir))
                {
                    ProtoLogBox.Text += "\n[Manager] Protos folder not found.";
                    return;
                }

                var files = Directory.GetFiles(protoDir, "*.proto", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    ProtoLogBox.Text += "\n[Manager] No .proto files found in Protos folder. Clearing.";
                    ProtoLoaderManager.Instance.Clear();
                    PacketSelectorControl.Refresh();
                    return;
                }

                _protoFolderPath = protoDir;
                await ProcessProtosAsync(files);
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] ReloadProto: {ex.Message}", Brushes.Red);
            }
        }

        private async Task ProcessProtosAsync(string[] protoFiles)
        {
            try
            {
                ProtoLogBox.Text += $"\n[Manager] Processing {protoFiles.Length} files...";
                ProtoLogBox.ScrollToEnd();
                AppendLog($"[Proto] Processing {protoFiles.Length} files...", Brushes.MediumPurple);

                // Use Workspace Path if available, otherwise fallback
                var targetDir = !string.IsNullOrEmpty(_workspacePath)
                    ? Path.Combine(_workspacePath, "Protos", "ProtoGen")
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Protos", "ProtoGen");

                // Stop active connections effectively to release assembly references
                if (_networkService.IsConnected)
                {
                    _networkService.Disconnect();
                    AppendLog("[Manager] Client disconnected for reload.", Brushes.Yellow);
                }

                if (_proxyServer != null && _proxyServer.IsStarted)
                {
                    _proxyServer.Stop();
                    _proxyServer.Dispose();
                    _proxyServer = null;
                    Dispatcher.Invoke(() => ProxyStartBtn.Content = "프록시 시작 (Start Proxy)");
                    AppendProxyLog("[Manager] Proxy stopped for reload.");
                }

                // Force GC to help unload
                GC.Collect();
                GC.WaitForPendingFinalizers();

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                // Clean old CS files to avoid duplicates/stale files
                var oldCs = Directory.GetFiles(targetDir, "*.cs");
                foreach (var f in oldCs)
                {
                    try
                    {
                        File.Delete(f);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Proto] Failed to delete {Path.GetFileName(f)}: {ex.Message}");
                    }
                }

                // Track loaded files
                _loadedProtoFiles.Clear();
                foreach (var protoPath in protoFiles)
                {
                    _loadedProtoFiles.Add(protoPath);
                }

                // Compile all proto files at once (handles imports correctly)
                var compiler = new ProtoCompiler();
                try
                {
                    ProtoLogBox.Text += $"\n  - Compiling {protoFiles.Length} proto files...";
                    compiler.CompileProtosToCSharp(protoFiles, targetDir);
                    ProtoLogBox.Text += $"\n  - Proto to C# conversion complete.";
                }
                catch (Exception ex)
                {
                    ProtoLogBox.Text += $"\n[Error] Proto compilation failed: {ex.Message}";
                    AppendLog($"[Proto Error] {ex.Message}", Brushes.Red);
                    return;
                }

                // Compile All Generated CS -> Single Protos.dll
                var csFiles = Directory.GetFiles(targetDir, "*.cs");
                if (csFiles.Length > 0)
                {
                    ProtoLogBox.Text += $"\n[Manager] Building Protos.dll from {csFiles.Length} sources...";

                    // Single Protos.dll path
                    var outputDll = Path.Combine(!string.IsNullOrEmpty(_workspacePath) ? _workspacePath : targetDir, "Protos.dll");

                    // Unload previous workspace context (both Proto and Script)
                    if (_workspaceAssemblyContext != null)
                    {
                        ProtoLoaderManager.Instance.Clear();
                        _protoAssembly = null;
                        _scriptAssembly = null;
                        _workspaceAssemblyContext.Unload();
                        _workspaceAssemblyContext = null;
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        ProtoLogBox.Text += $"\n  - Previous assemblies unloaded.";
                    }

                    // Delete old DLL if exists
                    if (File.Exists(outputDll))
                    {
                        try
                        {
                            File.Delete(outputDll);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Proto] Failed to delete old Protos.dll: {ex.Message}");
                        }
                    }

                    // Compile all CS files directly to Protos.dll
                    await _scriptLoader.CompileFilesToDllAsync(
                        csFiles, null,
                        msg => ProtoLogBox.Text += $"\n  {msg}",
                        assemblyName: "Protos",
                        outputPath: outputDll);

                    // Create new workspace context
                    _workspaceAssemblyContext = new UnloadableAssemblyContext();

                    // Load Protos.dll
                    _protoAssembly = _workspaceAssemblyContext.LoadFromFile(outputDll);

                    var messageTypes = _protoAssembly.GetTypes()
                        .Where(t => typeof(Google.Protobuf.IMessage).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                        .ToList();

                    foreach (var type in messageTypes)
                        ProtoLoaderManager.Instance.RegisterPacket(type);

                    ProtoLogBox.Text += $"\n[Manager] Protos.dll updated ({messageTypes.Count} messages)";
                    AppendLog($"[Proto] Loaded {messageTypes.Count} message types", Brushes.Green);

                    // Script needs recompilation to use new Proto types
                    var scriptDll = Path.Combine(!string.IsNullOrEmpty(_workspacePath) ? _workspacePath : targetDir, "Script.dll");
                    if (File.Exists(scriptDll))
                    {
                        _scriptAssembly = null;
                        ProtoLogBox.Text += $"\n[Manager] Proto changed. Please recompile Scripts.";
                        AppendLog("[Proto] Script 재컴파일 필요", Brushes.Orange);
                    }
                }
                else
                {
                    ProtoLogBox.Text += $"\n[Manager] No C# files generated.";
                }

                // Update UI (Proto Manager List)
                ProtoFileListBox.ItemsSource = null;
                ProtoFileListBox.ItemsSource = _loadedProtoFiles;

                // Refresh PacketSelector
                PacketSelectorControl.Refresh();


                // Update ScriptEditorWindow Intellisense if open
                if (_scriptEditorWindow != null && _scriptEditorWindow.IsLoaded)
                {
                    var types = new List<Type>
                    {
                        typeof(ScriptGlobals),
                        typeof(IScriptStateStore),
                        typeof(IScriptLogger),
                    };

                    types.AddRange(ProtoLoaderManager.Instance.PacketsByName.Values.Select(p => p.Type));

                    var json = CompletionService.GenerateCompletionJson(types);
                    _ = _scriptEditorWindow.UpdateCompletionsAsync(json);
                    ProtoLogBox.Text += $"\n[Manager] Script editor intellisense updated.";
                }

                ProtoLogBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                ProtoLogBox.Text += $"\n[Critical Error] {ex.Message}";
                AppendLog($"[Proto Error] {ex.Message}", Brushes.Red);
            }
        }

        #endregion

        private void ProxyStartBtn_Click(object sender, RoutedEventArgs e) => _ = ProxyStartBtn_ClickAsync();

        private async Task ProxyStartBtn_ClickAsync()
        {
            if (_proxyServer != null && _proxyServer.IsStarted)
            {
                // Stop Proxy
                _proxyServer.Stop();
                _proxyServer.Dispose();
                _proxyServer = null;

                ProxyStartBtn.Content = "프록시 시작 (Start Proxy)";
                AppendProxyLog("Proxy Stopped.");
                return;
            }

            // Start Proxy
            if (_scriptAssembly == null)
            {
                FluentMessageBox.ShowError("스크립트를 먼저 컴파일해 주세요.");
                return;
            }

            if (!int.TryParse(ProxyLocalPortBox.Text, out var localPort) ||
                !int.TryParse(ProxyTargetPortBox.Text, out var targetPort))
            {
                FluentMessageBox.ShowError("포트 번호가 올바르지 않습니다.");
                return;
            }

            var targetIp = ProxyTargetIpBox.Text;

            try
            {
                await StartProxyServerAsync(localPort, targetIp, targetPort);
                ProxyStartBtn.Content = "프록시 중지 (Stop Proxy)";
            }
            catch (Exception ex)
            {
                FluentMessageBox.ShowError($"프록시 시작 실패: {ex.Message}");
                AppendProxyLog($"Start Failed: {ex.Message}");
            }
        }

        private void AppendProxyLog(string msg)
        {
            // Simple text append
            ProxyLogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            ProxyLogBox.ScrollToEnd();
        }
    }
}