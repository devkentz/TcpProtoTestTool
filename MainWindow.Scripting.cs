using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Collections.ObjectModel;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;
using ProtoTestTool.Services;

namespace ProtoTestTool
{
    public class ToolScriptLogger : IScriptLogger
    {
        private readonly Action<string, SolidColorBrush> _logAction;

        public ToolScriptLogger(Action<string, SolidColorBrush> logAction)
        {
            _logAction = logAction;
        }

        public void Info(string message) => _logAction($"[INFO] {message}", Brushes.White);
        public void Warn(string message) => _logAction($"[WARN] {message}", Brushes.Orange);
        public void Error(string message) => _logAction($"[ERROR] {message}", Brushes.Red);
    }

    public class ToolClientApi : IClientApi
    {
        private readonly MainWindow _window;

        public ToolClientApi(MainWindow window)
        {
            _window = window;
        }

        public void Send<TMessage>(TMessage message)
        {
            // Delegate to MainWindow's Send logic
            // Since we are moving to PacketConvertor/Codec, we should use that.
            // For now, let's just use the SendPacket functionality if exposed, 
            // or we might need to expose a method in MainWindow.
            _window.Dispatcher.Invoke(() =>
            {
                // Using reflection or checking type to send?
                // Spec says: Send<TMessage>(message).
                // Implementation: serialized -> send.

                // TODO: Connect this to actual Send logic.
                // _window.SendPacket(message); 
                // For now, logging intention.
                _window.AppendLog($"[ClientApi] Sending {message}", Brushes.LightGreen);
            });
        }

        public void Delay(int milliseconds)
        {
            System.Threading.Thread.Sleep(milliseconds);
        }
    }

    public partial class MainWindow
    {
        private ProxyServer? _proxyServer;
        private ProxyInterceptorPipeline? _proxyPipeline; // Hot Reload Support
        private IScriptStateStore? _scriptState;
        private IClientPacketInterceptor? _clientInterceptor;

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

                // Hot Reload Step 1: Clear old references from Pipeline to allow Unload
                // This is crucial because _proxyPipeline holds instances from _scriptAssembly
                if (_proxyPipeline != null)
                {
                    _proxyPipeline.Clear();
                }

                _clientInterceptor = null;

                // Unload previous workspace context (both Proto and Script)
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

                // Delete old Script.dll before collecting references
                var outputDll = Path.Combine(workspacePath, "Script.dll");
                if (File.Exists(outputDll))
                {
                    try { File.Delete(outputDll); } catch { }
                }

                // Collect References
                var refs = new List<string>();

                // Add all DLLs in workspace root (e.g. Protos.dll), excluding Script.dll
                var dlls = Directory.GetFiles(workspacePath, "*.dll").ToList();

                // Add Libs folder (NuGet packages)
                var libsDir = Path.Combine(scriptsDir, "Libs");
                if (Directory.Exists(libsDir))
                {
                    dlls.AddRange(Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories));
                }

                var generatedDllPattern = new System.Text.RegularExpressions.Regex(@".+\.[a-fA-F0-9]{8}\.dll$");

                foreach (var dll in dlls)
                {
                    var fileName = Path.GetFileName(dll);
                    // Exclude Script.dll and generated temp files
                    if (!generatedDllPattern.IsMatch(dll) &&
                        !fileName.Equals("Script.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        refs.Add(dll);
                    }
                }

                // Add Protos.dll specifically if it exists in ProtoGen (fallback)
                var protoGenDir = Path.Combine(workspacePath, "ProtoGen");
                if (Directory.Exists(protoGenDir))
                {
                    var protoDlls = Directory.GetFiles(protoGenDir, "*.dll", SearchOption.AllDirectories);
                    foreach (var p in protoDlls)
                    {
                        if (!refs.Contains(p) && !generatedDllPattern.IsMatch(p))
                        {
                            refs.Add(p);
                        }
                    }
                }

                // Compile All Scripts directly to Script.dll
                await _scriptLoader.CompileFilesToDllAsync(
                    scriptFiles, refs,
                    (msg) => logAction(msg, Brushes.Gray),
                    assemblyName: "Script",
                    outputPath: outputDll);

                logAction("Compilation Success! Loading Assembly...", Brushes.DeepSkyBlue);

                // Create new workspace context
                _workspaceAssemblyContext = new UnloadableAssemblyContext();

                // Load Protos.dll first (if exists) so Script can reference it
                var protosDll = Path.Combine(workspacePath, "Protos.dll");
                if (File.Exists(protosDll))
                {
                    _protoAssembly = _workspaceAssemblyContext.LoadFromFile(protosDll);

                    // Re-register proto types
                    var messageTypes = _protoAssembly.GetTypes()
                        .Where(t => typeof(Google.Protobuf.IMessage).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                    foreach (var type in messageTypes)
                        ProtoLoaderManager.Instance.RegisterPacket(type);
                }

                // Load Script.dll
                var assembly = _workspaceAssemblyContext.LoadFromFile(outputDll);
                _scriptAssembly = assembly;

                // Find Registry and Codec
                var regType = assembly.GetTypes().FirstOrDefault(t => typeof(IPacketRegistry).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (regType == null) throw new Exception("IPacketRegistry implementation not found in scripts.");
                var registry = (IPacketRegistry)Activator.CreateInstance(regType)!;

                var codecType = assembly.GetTypes().FirstOrDefault(t => typeof(IPacketCodec).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (codecType == null) throw new Exception("IPacketCodec implementation not found in scripts.");
                var codec = (IPacketCodec)Activator.CreateInstance(codecType)!;

                // Init Globals
                if (_scriptState == null) _scriptState = new ScriptStateStore();
                
                var toolLogger = new ToolScriptLogger((msg, color) => { Dispatcher.Invoke(() => AppendLog(msg, color)); });
                var clientApi = new ToolClientApi(this);

                ScriptGlobals.Initialize(_scriptState, toolLogger);
                ScriptGlobals.SetApis(clientApi, null);
                ScriptGlobals.SetServices(registry, codec);

                // Find Interceptors (Common)
                var interceptorTypes = assembly.GetTypes()
                        .Where(t => typeof(IProxyPacketInterceptor).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                        .ToList();

                // Hot Reload Step 2: Update existing pipeline if active
                if (_proxyPipeline != null)
                {
                    foreach (var t in interceptorTypes)
                    {
                        var interceptor = (IProxyPacketInterceptor)Activator.CreateInstance(t)!;
                        _proxyPipeline.Add(interceptor);
                    }
                    if (_proxyServer != null && _proxyServer.IsStarted)
                    {
                        logAction($"[HotReload] Updated Proxy Interceptors ({interceptorTypes.Count})", Brushes.LimeGreen);
                    }
                }

                // Find Client Interceptor (Optional)
                var clientInterceptorType = assembly.GetTypes().FirstOrDefault(t => typeof(IClientPacketInterceptor).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (clientInterceptorType != null)
                {
                    _clientInterceptor = (IClientPacketInterceptor) Activator.CreateInstance(clientInterceptorType)!;
                }
                else
                {
                    _clientInterceptor = null;
                }

                RefreshPacketList();
                await Dispatcher.InvokeAsync(() => _ = LoadHeaderJsonAsync());

                // Update Intellisense
                if (true) 
                {
                    var types = new List<Type> 
                    { 
                        typeof(ScriptGlobals), 
                        typeof(IScriptStateStore), 
                        typeof(IScriptLogger),
                        typeof(IClientApi),
                        typeof(IProxyApi)
                    };
                    
                    if (ScriptGlobals.Registry != null)
                        types.AddRange(ScriptGlobals.Registry.GetMessageTypes());
                    
                     // Add types from compiled assembly too?
                     types.AddRange(assembly.GetTypes().Where(t => t.IsPublic));

                    var json = ProtoTestTool.Services.CompletionService.GenerateCompletionJson(types);
                    logAction($"[Intellisense] Updated metadata.", Brushes.Gray);

                     Dispatcher.Invoke(() => 
                     {
                         if (_scriptEditorWindow != null && _scriptEditorWindow.IsLoaded)
                         {
                             _ = _scriptEditorWindow.UpdateCompletionsAsync(json);
                         }
                     });
                }
            }
            catch (Exception ex)
            {
                logAction($"Error:\n{ex.Message}", Brushes.Red);
            }
        }

        // ...

        private Task StartProxyServerAsync(int localPort, string targetIp, int targetPort)
        {
            return Task.Run(() =>
            {
                try
                {
                    var assembly = _scriptAssembly;
                    if (assembly == null) throw new Exception("Script assembly not loaded.");

                    // 1. Get Codec from Globals
                    if (ScriptGlobals.Codec == null) throw new Exception("IPacketCodec이 초기화되지 않았습니다. (Compile First)");
                    var codec = ScriptGlobals.Codec;

                    // 2. Find Interceptors
                    _proxyPipeline = new ProxyInterceptorPipeline(); // Assign to field
                    var interceptorTypes = assembly.GetTypes()
                        .Where(t => typeof(IProxyPacketInterceptor).IsAssignableFrom(t) && !t.IsAbstract)
                        .ToList();

                    // Update UI List
                    Dispatcher.Invoke(() =>
                    {
                        InterceptorListBox.Items.Clear();
                        foreach (var t in interceptorTypes)
                            InterceptorListBox.Items.Add(t.Name);
                    });

                    foreach (var t in interceptorTypes)
                    {
                        var interceptor = (IProxyPacketInterceptor) Activator.CreateInstance(t)!;
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


        // _headerAssembly is no longer separate

        private void RefreshPacketList()
        {
            if (ScriptGlobals.Registry == null) return;
            
            // Bridge: Register script types to Singleton manager so PacketSelector sees them
            foreach (var type in ScriptGlobals.Registry.GetMessageTypes())
            {
                 ProtoLoaderManager.Instance.RegisterPacket(type);
            }

            PacketSelectorControl.Refresh();
        }

        public void InitializeWorkspaceFiles(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return;

            var scriptsDir = Path.Combine(workspacePath, "Scripts");
            if (!Directory.Exists(scriptsDir)) Directory.CreateDirectory(scriptsDir);

            // Create default .cs files
            CreateIfMissing(scriptsDir, "PacketRegistry.cs", "PacketRegistry");
            CreateIfMissing(scriptsDir, "PacketHeader.cs", "PacketHeader");
            CreateIfMissing(scriptsDir, "PacketCodec.cs", "PacketCodec");
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
                    File.WriteAllText(path, GetDefaultTemplate(templateName));
                    Dispatcher.Invoke(() => AppendLog($"[Workspace] Created {fileName}", Brushes.Green));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"[Error] Failed to create {fileName}: {ex.Message}", Brushes.Red));
                }
            }
        }

        private string GetDefaultTemplate(string featureName)
        {
            return featureName switch
            {
                "PacketRegistry" =>
@"using System;
using System.Collections.Generic;
using ProtoTestTool.ScriptContract;
using Google.Protobuf;

public class PacketRegistry : IPacketRegistry
{
    private readonly Dictionary<int, Type> _idToType = new Dictionary<int, Type>();
    private readonly Dictionary<Type, int> _typeToId = new Dictionary<Type, int>();

    public void Register(int id, Type type)
    {
        _idToType[id] = type;
        _typeToId[type] = id;
    }

    public IEnumerable<Type> GetMessageTypes() => _idToType.Values;

    public Type? GetMessageType(int id) => _idToType.TryGetValue(id, out var type) ? type : null;

    public int GetMessageId(Type type) => _typeToId.TryGetValue(type, out var id) ? id : 0;

    public MessageParser GetParserById(int id)
    {
        throw new NotImplementedException();
    }
}",
                "PacketHeader" =>
@"using System;
using ProtoTestTool.ScriptContract;

public class Header : IHeader
{
    public string ToJsonString()
    {
        throw new NotImplementedException();
    }
}",
                "PacketCodec" =>
@"using System;
using System.Buffers;
using ProtoTestTool.ScriptContract;

public class PacketCodec : IPacketCodec
{
    public int TryDecode(ref ReadOnlySpan<byte> span, out Packet? packet)
    {
        throw new NotImplementedException();
    }

    public ReadOnlyMemory<byte> Encode(Packet packet)
    {
        throw new NotImplementedException();
    }
}",
                "PacketInterceptor" =>
@"using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

public class Interceptor : IProxyPacketInterceptor
{
    public ValueTask OnInboundAsync(ProxyPacketContext context)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask OnOutboundAsync(ProxyPacketContext context)
    {
        return ValueTask.CompletedTask;
    }
}",
                _ => "// Not found"
            };
        }



        #region Proto Manager

        private ObservableCollection<string> _loadedProtoFiles = new ObservableCollection<string>();
        // Note: _loadedMessageTypes would normally be derived from registry, 
        // but here we can track what we just imported.

        private void LoadProtoFileBtn_Click(object sender, RoutedEventArgs e) => _ = LoadProtoFileBtn_ClickAsync();
        private async Task LoadProtoFileBtn_ClickAsync()
        {
            try
            {
                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Proto files (*.proto)|*.proto",
                    Title = "Proto 파일 선택 (Select Proto)",
                    Multiselect = true
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    await ProcessProtosAsync(openFileDialog.FileNames);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] LoadProtoFile: {ex.Message}", Brushes.Red);
            }
        }



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
                if (_client != null && _client.IsConnected)
                {
                    _client.Disconnect();
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
                foreach (var f in oldCs) { try { File.Delete(f); } catch { } }

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
                        try { File.Delete(outputDll); }
                        catch { /* May be locked, will overwrite */ }
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
                        typeof(IClientApi),
                        typeof(IProxyApi)
                    };

                    // Add proto message types
                    types.AddRange(ProtoLoaderManager.Instance.PacketsByMsgId.Values.Select(p => p.Type));

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