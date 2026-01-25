using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;

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
        private ReverseProxyServer? _proxyServer;
        private IScriptStateStore? _scriptState;
        private IClientPacketInterceptor? _clientInterceptor;

        // Document IDs for Reference Updates





        public async Task CompileScriptsAsync(string dir, Action<string, Brush> logAction)
        {
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                logAction("Starting Compilation (from Disk)...", Brushes.White);

                // 1. Compile Registry
                var regPath = Path.Combine(dir, "PacketRegistry.cs");
                if (!File.Exists(regPath)) regPath = Path.Combine(dir, "PacketRegistry.csx");
                if (!File.Exists(regPath)) throw new FileNotFoundException("PacketRegistry.cs/.csx 없음");

                logAction("Packet Registry 컴파일 중...", Brushes.White);
                
                var regRefs = new List<string>();
                
                // Add all DLLs in workspace (e.g. Test.dll, Protos.dll)
                // Filter out generated script dlls (e.g. PacketRegistry.1a2b3c4d.dll) to prevent "Metadata not found" errors
                var dlls = Directory.GetFiles(dir, "*.dll").ToList();
                
                // Add Libs folder (NuGet packages)
                var libsDir = Path.Combine(dir, "Libs");
                if (Directory.Exists(libsDir))
                {
                    dlls.AddRange(Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories));
                }

                var generatedDllPattern = new System.Text.RegularExpressions.Regex(@".+\.[a-fA-F0-9]{8}\.dll$");

                foreach (var dll in dlls)
                {
                    if (!generatedDllPattern.IsMatch(dll))
                    {
                        regRefs.Add(dll);
                    }
                } 

                var regDllPath = await _scriptLoader.CompileToDllAsync(regPath, regRefs, (msg) => logAction(msg, Brushes.Gray));

                // 1.5 Compile Header
                var headerPath = Path.Combine(dir, "PacketHeader.cs");
                if (!File.Exists(headerPath)) headerPath = Path.Combine(dir, "PacketHeader.csx");
                string headerDllPath = "";
                if (File.Exists(headerPath))
                {
                     logAction("Packet Header 컴파일 중...", Brushes.White);
                     headerDllPath = await _scriptLoader.CompileToDllAsync(headerPath, new [] { regDllPath }, (msg) => logAction(msg, Brushes.Gray));
                }

                // 2. Compile Serializer
                var serPath = Path.Combine(dir, "PacketSerializer.cs");
                if (!File.Exists(serPath)) serPath = Path.Combine(dir, "PacketSerializer.csx");
                if (!File.Exists(serPath)) throw new FileNotFoundException("PacketSerializer.cs/.csx 없음");

                logAction("Packet Serializer 컴파일 중...", Brushes.White);
                var serRefs = new List<string> { regDllPath };
                if (!string.IsNullOrEmpty(headerDllPath)) serRefs.Add(headerDllPath);
                
                var serDllPath = await _scriptLoader.CompileToDllAsync(serPath, serRefs, (msg) => logAction(msg, Brushes.Gray));

                // 3. Compile Context (User Logic)
                var mainPath = Path.Combine(dir, "PacketHandler.cs");
                if (!File.Exists(mainPath)) mainPath = Path.Combine(dir, "PacketHandler.csx");
                var buildPath = Path.Combine(dir, "PacketHandler.Build.cs"); // Intermediate build file also .cs

                if (!File.Exists(mainPath))
                {
                    // If neither exists, create default .cs
                    CreateIfMissing(dir, "PacketHandler.cs", "MyScriptContext");
                    mainPath = Path.Combine(dir, "PacketHandler.cs");
                }

                var mainCode = await File.ReadAllTextAsync(mainPath);
                // Remove #load
                var lines = mainCode.Split(new[] {"\r\n", "\n"}, StringSplitOptions.None);
                var sb = new System.Text.StringBuilder();
                
                // Add #line directive to map back to original file for debugging
                // Escape backslashes for the string literal
                var escapedPath = mainPath.Replace("\\", "\\\\");
                sb.AppendLine($"#line 1 \"{escapedPath}\"");

                foreach (var line in lines)
                {
                    if (line.TrimStart().StartsWith("#load"))
                    {
                        sb.AppendLine($"// {line}"); // Comment out to preserve line number
                    }
                    else
                    {
                        sb.AppendLine(line);
                    }
                }
                
                await File.WriteAllTextAsync(buildPath, sb.ToString());

                logAction("User Logic 컴파일 중...", Brushes.White);

                // Collect References
                var protoGenDir = Path.Combine(dir, "ProtoGen");
                var protoRefs = new List<string> {regDllPath, serDllPath};
                if (!string.IsNullOrEmpty(headerDllPath)) protoRefs.Add(headerDllPath);

                if (Directory.Exists(protoGenDir))
                {
                     // Check for Protos.dll in Workspace
                     var protoDllPath = Path.Combine(dir, "Protos.dll");
                     if (File.Exists(protoDllPath))
                     {
                         protoRefs.Add(protoDllPath);
                     }
                     else
                     {
                         // Fallback or explicit check
                         var protoDlls = Directory.GetFiles(protoGenDir, "*.dll", SearchOption.AllDirectories);
                         protoRefs.AddRange(protoDlls);
                     }
                }

                // Load Registry & Codec Instances
                var regAssembly = System.Reflection.Assembly.LoadFrom(regDllPath);
                var regType = regAssembly.GetTypes().FirstOrDefault(t => typeof(IPacketRegistry).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (regType == null) throw new Exception($"IPacketRegistry implementation not found in {regDllPath}");
                var registry = (IPacketRegistry)Activator.CreateInstance(regType)!;

                var serAssembly = System.Reflection.Assembly.LoadFrom(serDllPath);
                var codecType = serAssembly.GetTypes().FirstOrDefault(t => typeof(IPacketCodec).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (codecType == null) throw new Exception($"IPacketCodec implementation not found in {serDllPath}");
                var codec = (IPacketCodec)Activator.CreateInstance(codecType)!;

                // Init Globals
                if (_scriptState == null) _scriptState = new ScriptStateStore();

                // Runtime Logger (still routes to Main LogBox via dispatcher, kept as is or passed?)
                // The runtime logger is for "during execution". 
                // We'll keep the current behavior: toolLogger writes to Main LogBox.
                // The compilation logger (passed in) writes to ScriptEditor LogBox.
                var toolLogger = new ToolScriptLogger((msg, color) => { Dispatcher.Invoke(() => AppendLog(msg, color)); });
                var clientApi = new ToolClientApi(this);

                ScriptGlobals.Initialize(_scriptState, toolLogger);
                ScriptGlobals.SetApis(clientApi, null);
                ScriptGlobals.SetServices(registry, codec);

                // Load User Logic (Compile to Disk to generate PDB for debugging)
                var contextDllPath = await _scriptLoader.CompileToDllAsync(buildPath, protoRefs, (msg) => logAction(msg, Brushes.Gray));
                var contextAssembly = System.Reflection.Assembly.LoadFrom(contextDllPath);
                
                _scriptAssembly = contextAssembly;

                if (!string.IsNullOrEmpty(headerDllPath))
                {
                    _headerAssembly = System.Reflection.Assembly.LoadFrom(headerDllPath);
                }

                // Find Interceptors
                var clientInterceptorType = _scriptAssembly.GetTypes().FirstOrDefault(t => typeof(IClientPacketInterceptor).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (clientInterceptorType != null)
                {
                    _clientInterceptor = (IClientPacketInterceptor) Activator.CreateInstance(clientInterceptorType)!;
                }
                else
                {
                    _clientInterceptor = null;
                }

                logAction("Compilation Success!", Brushes.DeepSkyBlue);
                RefreshPacketList();
                
                // Update Intellisense (Requires Logic for Context)
                if (true) // Keep scope
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

                    var json = ProtoTestTool.Services.CompletionService.GenerateCompletionJson(types);
                    logAction($"[Intellisense] Generated {json.Length} bytes of metadata.", Brushes.Gray);

                     Dispatcher.Invoke(() => 
                     {
                         if (_scriptEditorWindow != null && _scriptEditorWindow.IsLoaded)
                         {
                             _ = _scriptEditorWindow.UpdateCompletionsAsync(json);
                         }
                         else 
                         {
                             logAction("[Intellisense] ScriptEditorWindow not loaded/open.", Brushes.Orange);
                         }
                     });
                }
            }
            catch (Exception ex)
            {
                logAction($"Error:\n{ex.Message}", Brushes.Red);
            }
        }


        private System.Reflection.Assembly? _scriptAssembly;
        private System.Reflection.Assembly? _headerAssembly;



        private void RefreshPacketList()
        {
            PacketListBox.ItemsSource = null;
            if (ScriptGlobals.Registry == null) return;

            var types = ScriptGlobals.Registry.GetMessageTypes()
                .OrderBy(t => t.Name)
                .ToList();
            
            // Protos.dll message processing should use ProtoLoaderManager logic
            // But PacketRegistry might have manual entries.
            // Let's stick to what we have or sync with ProtoLoaderManager?
            // Actually, PacketRegistry MIGHT be obsolete if we fully rely on ProtoLoaderManager?
            // But PacketRegistry maps ID <-> Type. ProtoLoaderManager maps Name <-> Type.
            // We need Registry for ID mapping.
            
            // Merging Lists: Registry Types + ProtoLoader Types?
            // For now, Dictionary-based Registry is mostly for ID mapping.
            // The ListBox shows types from Registry.
            
            PacketListBox.ItemsSource = types;
        }





        public void InitializeWorkspaceFiles(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return;

            // Updated to use .cs by default
            CreateIfMissing(workspacePath, "PacketRegistry.cs", "PacketRegistry");
            CreateIfMissing(workspacePath, "PacketHeader.cs", "PacketHeader");
            CreateIfMissing(workspacePath, "PacketSerializer.cs", "PacketSerializer");
            CreateIfMissing(workspacePath, "PacketHandler.cs", "MyScriptContext");
            
            // Create default config
            var configPath = Path.Combine(workspacePath, "workspace_config.json");
            if (!File.Exists(configPath))
            {
                var defaultConfig = new WorkspaceConfig(); // Defaults: 127.0.0.1:9000
                defaultConfig.Save(workspacePath);
                Dispatcher.Invoke(() => AppendLog($"[Workspace] Created workspace_config.json", Brushes.Green));
            }
        }



        private void CreateIfMissing(string dir, string fileName, string templateName)
        {
            var path = Path.Combine(dir, fileName);
            // Check for both .cs and .csx if creating? No, just check if target exists.
            // But if we are "initializing" we might want to respect existing .csx
            // Logic: Create 'PacketRegistry.cs' ONLY if 'PacketRegistry.cs' AND 'PacketRegistry.csx' do not exist.
            
            var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
            var csxPath = Path.Combine(dir, nameNoExt + ".csx");
            var csPath = Path.Combine(dir, nameNoExt + ".cs");
            
            if (!File.Exists(csxPath) && !File.Exists(csPath))
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
}",
                "PacketHeader" =>
                    @"using System;
using Newtonsoft.Json;
using ProtoTestTool.ScriptContract;

public class Header : IHeader
{
    public int MsgId { get; set; }
    public byte Flags { get; set; }
    
    public string ToJsonString()
    {
        return JsonConvert.SerializeObject(this);
    }
}",
                "PacketSerializer" =>
                    @"using System;
using System.Buffers;
using ProtoTestTool.ScriptContract;

public class PacketCodec : IPacketCodec
{
    // Default: 4-byte Length + Payload
    // Format: [Length(4)][MsgId(4)][Payload...]
    
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out object? message)
    {
        message = null;
        if (buffer.Length < 4) return false;

        var set = buffer.Slice(0, 4);
        Span<byte> header = stackalloc byte[4];
        set.CopyTo(header);
        var len = BitConverter.ToInt32(header);

        if (buffer.Length < 4 + len) return false;

        // Check Registry for MsgId (Assumes [Len][MsgId][Protobuf])
        var payloadSeq = buffer.Slice(4, len);
        
        // Example: Peeking MsgId at offset 4 (after length)
        if (payloadSeq.Length >= 4)
        {
             var msgIdSeq = payloadSeq.Slice(0, 4);
             Span<byte> msgIdBytes = stackalloc byte[4];
             msgIdSeq.CopyTo(msgIdBytes);
             int msgId = BitConverter.ToInt32(msgIdBytes);
             
             // TODO: Use a Registry if available or Reflection lookup
             // System.Console.WriteLine($""MsgId: {msgId}"");
        }

        // Return raw wrapper for now or implement ProtoParser call
        message = new { RawSize = len, Data = payloadSeq.ToArray() };

        buffer = buffer.Slice(4 + len);
        return true;
    }

    public ReadOnlyMemory<byte> Encode(object message)
    {
        // Placeholder
        return new byte[4]; 
    }
}",
                "MyScriptContext" =>
                    @"using System;
using ProtoTestTool.ScriptContract;
using System.Threading.Tasks;

// ****************************************
// *      USER IMPLEMENTATION AREA        *
// ****************************************

public class MyInterceptor : IProxyPacketInterceptor
{
    public ValueTask OnInboundAsync(ProxyPacketContext context)
    {
        // Example: Count packets using State
        if (ScriptGlobals.State.TryGet<int>(""PacketCount"", out var count))
        {
            ScriptGlobals.State.Set(""PacketCount"", count + 1);
        }
        else
        {
            ScriptGlobals.State.Set(""PacketCount"", 1);
        }

        ScriptGlobals.Log.Info($""[Script] Packet Received. Count: {ScriptGlobals.State.Get<int>(""PacketCount"")}"");
        
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

        private void LoadProtoFolderBtn_Click(object sender, RoutedEventArgs e) => _ = LoadProtoFolderBtn_ClickAsync();
        private async Task LoadProtoFolderBtn_ClickAsync()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Proto 폴더 선택 (Select Folder)"
                };

                if (dialog.ShowDialog() == true)
                {
                    var files = Directory.GetFiles(dialog.FolderName, "*.proto", SearchOption.AllDirectories);
                    if (files.Length == 0)
                    {
                        ProtoLogBox.Text += $"\n[Manager] No .proto files found in {dialog.FolderName}";
                        return;
                    }

                    _protoFolderPath = dialog.FolderName;
                    SaveWorkspaceConfiguration();

                    await ProcessProtosAsync(files);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] LoadProtoFolder: {ex.Message}", Brushes.Red);
            }
        }

        private void ReloadProtoBtn_Click(object sender, RoutedEventArgs e) => _ = ReloadProtoBtn_ClickAsync();
        private async Task ReloadProtoBtn_ClickAsync()
        {
            try
            {
                if (_loadedProtoFiles.Count == 0)
                {
                    ProtoLogBox.Text += "\n[Manager] No files to reload.";
                    return;
                }

                await ProcessProtosAsync(_loadedProtoFiles.ToArray());
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
                    ? Path.Combine(_workspacePath, "ProtoGen") 
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProtoGen");

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                
                // Clean old CS files to avoid duplicates/stale files
                var oldCs = Directory.GetFiles(targetDir, "*.cs");
                foreach(var f in oldCs) { try { File.Delete(f); } catch {} }

                var compiler = new ProtoCompiler();
                
                foreach (var protoPath in protoFiles)
                {
                    if (!_loadedProtoFiles.Contains(protoPath)) _loadedProtoFiles.Add(protoPath);

                    try
                    {
                        ProtoLogBox.Text += $"\n  - Compiling {Path.GetFileName(protoPath)}...";
                        compiler.CompileProtoToCSharp(protoPath, targetDir);
                    }
                    catch (Exception ex)
                    {
                        ProtoLogBox.Text += $"\n[Error] {Path.GetFileName(protoPath)}: {ex.Message}";
                        AppendLog($"[Proto Error] {Path.GetFileName(protoPath)}: {ex.Message}", Brushes.Red);
                    }
                }

                // Compile All Generated CS -> Single Protos.dll
                var csFiles = Directory.GetFiles(targetDir, "*.cs");
                if (csFiles.Length > 0)
                {
                    ProtoLogBox.Text += $"\n[Manager] Building Protos.dll from {csFiles.Length} sources...";
                    
                    // Create a combined source file or compile list
                    // scriptLoader.CompileToDllAsync takes a single file.
                    // We will merge them into a temp file "AllProtos.cs"
                    var mergedSource = Path.Combine(targetDir, "AllProtos.cs");
                    using (var sw = new StreamWriter(mergedSource))
                    {
                        foreach(var cs in csFiles)
                        {
                            var text = await File.ReadAllTextAsync(cs);
                            sw.WriteLine($"// File: {Path.GetFileName(cs)}");
                            sw.WriteLine(text);
                            sw.WriteLine();
                        }
                    }

                    // Compile Protos.dll
                    var outputDll = Path.Combine(!string.IsNullOrEmpty(_workspacePath) ? _workspacePath : targetDir, "Protos.dll");
                    
                    // We need to use ScriptLoader but pointing to the merged file
                    // Manually use ScriptLoader's internal logic or reuse it if it supports output path override?
                    // ScriptLoader.CompileToDllAsync generates a random name.
                    // Let's modify ScriptLoader or just rename the output.
                    
                    var compiledPath = await _scriptLoader.CompileToDllAsync(mergedSource, null);
                    
                    if (File.Exists(outputDll)) File.Delete(outputDll);
                    File.Copy(compiledPath, outputDll);
                    
                    // Load and Register
                    var assembly = System.Reflection.Assembly.LoadFrom(outputDll);
                    var messageTypes = assembly.GetTypes()
                        .Where(t => typeof(Google.Protobuf.IMessage).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                    ProtoLoaderManager.Instance.Clear();
                    
                    foreach (var type in messageTypes) 
                        ProtoLoaderManager.Instance.RegisterPacket(type);
                        
                    ProtoLogBox.Text += $"\n[Manager] Protos.dll updated ({messageTypes.Count()} messages)";
                }

                // Update UI (Proto Manager List)
                ProtoFileListBox.ItemsSource = _loadedProtoFiles;
                
                // Re-bind PacketListBox from ProtoLoaderManager
                PacketListBox.ItemsSource = ProtoLoaderManager.Instance.GetSendPackets();

                ProtoLogBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                ProtoLogBox.Text += $"\n[Critical Error] {ex.Message}";
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
                    var pipeline = new ProxyInterceptorPipeline();
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
                        pipeline.Add(interceptor);
                    }

                    // 3. Create Server
                    _proxyServer = new ReverseProxyServer("0.0.0.0", localPort, targetIp, targetPort, pipeline, codec);
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


        private void AppendProxyLog(string msg)
        {
            // Simple text append
            ProxyLogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            ProxyLogBox.ScrollToEnd();
        }
    }
}