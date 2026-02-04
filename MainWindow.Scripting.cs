using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Collections.ObjectModel;
using ProtoTestTool.Controls;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;
using ProtoTestTool.Services;
using ProtoTestTool.Services.ScriptBuilder;
using ProtoTestTool.Services.ProtoBuilder;
using ProtoTestTool.Models;
using ProtoTestTool.Views;

namespace ProtoTestTool
{
    public partial class MainWindow
    {
        private ProxyServer? _proxyServer;
        private ProxyInterceptorPipeline? _proxyPipeline; // Hot Reload Support
        private IScriptStateStore? _scriptState;

        // Assembly Manager & Builders
        private readonly AssemblyContextManager _assemblyManager = new();
        private ScriptBuilder? _scriptBuilder;
        private ProtoBuilder? _protoBuilder;

        private void InitializeBuilders()
        {
            _scriptBuilder = new ScriptBuilder(_assemblyManager);
            _protoBuilder = new ProtoBuilder(_assemblyManager);
        }

        // Accessors for Loaded Assemblies
        private Assembly? ScriptAssembly => _assemblyManager.ScriptAssembly;

        // Document IDs for Reference Updates
        public async Task CompileScriptsAsync(string workspacePath, Action<string, Brush> logAction)
        {
            if (_scriptBuilder == null) InitializeBuilders();

            var assembly = await _scriptBuilder!.CompileAsync(workspacePath, logAction);

            if (assembly != null)
            {
                await OnScriptAssemblyLoaded(assembly, logAction);
            }
        }

        private async Task OnScriptAssemblyLoaded(Assembly assembly, Action<string, Brush> logAction)
        {
            try
            {
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
                    throw new NotImplementedException($"[Registry] Using script-defined {nameof(IPacketRegistry)} not implemented");
                }

                var codecType = assembly.GetTypes().FirstOrDefault(t => typeof(IPacketCodec).IsAssignableFrom(t) && t is {IsAbstract: false, IsInterface: false});

                if (codecType == null)
                    throw new Exception("IPacketCodec implementation not found in scripts.");

                var codec = (IPacketCodec) Activator.CreateInstance(codecType)!;

                InitializeScriptGlobals(registry, codec);
                UpdateInterceptors(assembly, logAction);

                var protoAssembly = _assemblyManager.ProtoAssembly;
                if (protoAssembly != null)
                {
                    var protos = ProtobufHelper.GetIMessageTypes(protoAssembly);
                    ScriptGlobals.Registry.RegisterMessageType(protos);
                    var requestPackets = ScriptGlobals.Registry.GetMessageTypesRequest();

                    await Dispatcher.InvokeAsync(() => PacketSelectorControl.RefreshPackets(requestPackets));
                }
                
                
                await Dispatcher.InvokeAsync(() => _ = LoadHeaderJsonAsync());
            }
            catch (Exception ex)
            {
                logAction($"[Loader Error] {ex.Message}", Brushes.Red);
                FileLogger.Instance.Error("OnScriptAssemblyLoaded failed", ex);
            }
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

            if (sender.Equals(ProxyInterceptorSelector) && _proxyPipeline != null && _proxyServer is {IsStarted: true})
            {
                RebuildProxyPipeline();
            }
        }

        private void RebuildProxyPipeline()
        {
            if (_proxyPipeline == null)
                return;

            var activeItems = ProxyInterceptorSelector.GetActiveInterceptors();

            _proxyPipeline.Update(activeItems);

            AppendProxyLog($"Pipeline updated: {activeItems.Count} interceptor(s) active.");
        }




        private void StartProxyServer(int localPort, string targetIp, int targetPort)
        {
            try
            {
                var assembly = ScriptAssembly;
                if (assembly == null)
                    throw new Exception("Script assembly not loaded.");

                // 2. Find Interceptors
                var newPipeline = new ProxyInterceptorPipeline();

                // [수정 2] 불필요한 Task 리턴 제거하고 깔끔하게 호출
                Dispatcher.InvokeAsync(() => newPipeline.Update(ProxyInterceptorSelector.GetActiveInterceptors()));

                // 3. Create Server
                _proxyServer = new ProxyServer("0.0.0.0", localPort, targetIp, targetPort, newPipeline);
                _proxyServer.Start();

                _proxyPipeline = newPipeline;

                Dispatcher.Invoke(() => AppendProxyLog($"Proxy Started on {localPort} -> {targetIp}:{targetPort}"));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendProxyLog($"Error starting proxy: {ex.Message}"));
                FileLogger.Instance.Error("StartProxyServerAsync failed", ex);
                throw;
            }
        }


        public void InitializeWorkspaceFiles(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) 
                return;

            var scriptsDir = Path.Combine(workspacePath, BuildConstants.ScriptsFolder);
            
            if (!Directory.Exists(scriptsDir)) 
                Directory.CreateDirectory(scriptsDir);

            // Create default .cs files
            CreateIfMissing(scriptsDir, BuildConstants.FileNamePacketHeader, BuildConstants.TemplatePacketHeader);
            CreateIfMissing(scriptsDir, BuildConstants.FileNamePacketCodec, BuildConstants.TemplatePacketCodec);
            CreateIfMissing(scriptsDir, BuildConstants.FileNamePacketRegistry, BuildConstants.TemplatePacketRegistry);
            // CreateIfMissing(scriptsDir, BuildConstants.FileNamePacketInterceptor, BuildConstants.TemplatePacketInterceptor);

            var configPath = Path.Combine(workspacePath, BuildConstants.FileNameWorkspaceConfig);
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
                    FileLogger.Instance.Error($"Failed to create {fileName}", ex);
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
                    ? Path.Combine(_workspacePath, BuildConstants.ProtosFolder)
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BuildConstants.ProtosFolder);

                if (!Directory.Exists(protoDir))
                {
                    ProtoLogBox.Text += "\n[Manager] Protos folder not found.";
                    return;
                }

                var files = Directory.GetFiles(protoDir, "*.proto", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    ProtoLogBox.Text += "\n[Manager] No .proto files found in Protos folder. Clearing.";
                    // ProtoLoaderManager.Instance.Clear(); // REMOVED
                    PacketSelectorControl.LoadPackets();
                    return;
                }

                _protoFolderPath = protoDir;
                await ProcessProtosAsync(files);
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] ReloadProto: {ex.Message}", Brushes.Red);
                FileLogger.Instance.Error("ReloadProtoBtn_Click failed", ex);
            }
        }

        private async Task ProcessProtosAsync(string[] protoFiles)
        {
            if (_protoBuilder == null)
                InitializeBuilders();

            // Stop Connection
            if (_networkService.IsConnected)
            {
                _networkService.Disconnect();
                AppendLog("[Manager] Client disconnected for reload.", Brushes.Yellow);
            }

            if (_proxyServer is {IsStarted: true})
            {
                _proxyServer.Stop();
                _proxyServer.Dispose();
                _proxyServer = null;

                Dispatcher.Invoke(() => ProxyStartBtn.Content = "프록시 시작 (Start Proxy)");
                AppendProxyLog("[Manager] Proxy stopped for reload.");
            }

            var assembly = await _protoBuilder!.ProcessProtosAsync(
                protoFiles,
                _workspacePath,
                msg => ProtoLogBox.Text += msg,
                AppendLog);

            if (assembly != null)
            {
                // Update UI (Proto Manager List)
                _loadedProtoFiles.Clear();

                foreach (var protoPath in protoFiles)
                    _loadedProtoFiles.Add(protoPath);

                ProtoFileListBox.ItemsSource = null;
                ProtoFileListBox.ItemsSource = _loadedProtoFiles;

                // Refresh PacketSelector
                var types = ProtobufHelper.GetIMessageTypes(assembly);
                ScriptGlobals.Registry.RegisterMessageType(types);
                var requestPackets = ScriptGlobals.Registry.GetMessageTypesRequest();
                PacketSelectorControl.RefreshPackets(requestPackets);

                // Recompile Scripts if valid workspace
                if (!string.IsNullOrEmpty(_workspacePath))
                {
                    var scriptDll = Path.Combine(_workspacePath, BuildConstants.ScriptDllName);
                    if (File.Exists(scriptDll))
                    {
                        ProtoLogBox.Text += $"\n[Manager] Proto changed. Please recompile Scripts.";
                        AppendLog("[Proto] Script 재컴파일 필요", Brushes.Orange);
                    }
                }

                ProtoLogBox.ScrollToEnd();
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

            try
            {
                ProxyStartBtn.IsEnabled = false;

                // Start Proxy
                if (ScriptAssembly == null)
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

                StartProxyServer(localPort, targetIp, targetPort);
                ProxyStartBtn.Content = "프록시 중지 (Stop Proxy)";
            }
            catch (Exception ex)
            {
                FluentMessageBox.ShowError($"프록시 시작 실패: {ex.Message}");
                AppendProxyLog($"Start Failed: {ex.Message}");
                FileLogger.Instance.Error("ProxyStartBtn_Click failed", ex);
            }
            finally
            {
                ProxyStartBtn.IsEnabled = true;
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