using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Encodings.Web;
using System.Windows.Documents;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Win32;
using Newtonsoft.Json;
using ProtoTestTool.Controls;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;
using ProtoTestTool.Services;

namespace ProtoTestTool
{
    public partial class MainWindow
    {
        private INetworkService _networkService; // Use Interface
        private readonly ScriptLoader _scriptLoader = new();
        private readonly Network.ByteBuffer _receiveBuffer = new();

        public MainWindow()
        {
            InitializeComponent();
            _networkService = new NetworkService();

            _networkService.Connected += OnConnected;
            _networkService.Disconnected += OnDisconnected;
            _networkService.ErrorOccurred += OnError;
            _networkService.DataReceived += OnDataReceived;

            Closing += MainWindow_Closing;
            ResponseHeaderGrid.ItemsSource = _responseHeaders;
        }

        private void OnConnected()
        {
            Dispatcher.Invoke(() =>
            {
                AppendLog($"Connected to Server", Brushes.DeepSkyBlue);
                UpdateConnectionState(true);
            });
        }

        private void OnDisconnected()
        {
            Dispatcher.Invoke(() =>
            {
                AppendLog("Disconnected", Brushes.Orange);
                UpdateConnectionState(false);
            });
        }

        private void OnError(string err)
        {
            Dispatcher.Invoke(() =>
            {
                AppendLog($"Socket Error: {err}", Brushes.Red);
                UpdateConnectionState(false);
            });
        }


        private async void ConnectToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_networkService.IsConnected)
            {
                _networkService.Disconnect();
            }
            else
            {
                var ip = IpBox.Text;
                if (!int.TryParse(PortBox.Text, out var port))
                {
                    AppendLog("Invalid Port", Brushes.Red);
                    return;
                }

                SaveWorkspaceConfiguration();
                ConnectToggleBtn.IsEnabled = false;

                try
                {
                    AppendLog($"Connecting to {ip}:{port}...", Brushes.Gray);
                    await _networkService.ConnectAsync(ip, port);
                }
                catch (Exception ex)
                {
                    AppendLog($"Connection failed: {ex.Message}", Brushes.Red);
                    UpdateConnectionState(false);
                }
            }
        }

        private async void PacketSelectorControl_PacketSelected(object sender, RoutedEventArgs e)
        {
            var selected = PacketSelectorControl.SelectedPacket;
            if (selected == null)
            {
                await JsonEditorView.SetTextAsync("{}");
                SendBtn.IsEnabled = false;
                return;
            }

            try
            {
                // Ensure default json template is prepared on packet
                var json = selected.DefaultJsonString();
                // Binding will push JsonText into the editor; ensure UI gets updated
                // If editor not yet ready, explicitly set text as fallback
                if (JsonEditorView != null)
                {
                    await JsonEditorView.SetTextAsync(json);
                }

                SendBtn.IsEnabled = _networkService.IsConnected;
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] Failed to load packet template: {ex.Message}", Brushes.Red);
                SendBtn.IsEnabled = false;
            }
        }


        private async Task SendBtn_ClickAsync(object sender, RoutedEventArgs e)
        {
            if (!_networkService.IsConnected)
            {
                FluentMessageBox.ShowError("서버에 연결되지 않았습니다.");
                return;
            }

            var selectedPacket = PacketSelectorControl.SelectedPacket;
            if (selectedPacket == null)
            {
                FluentMessageBox.ShowError("패킷이 선택되지 않았습니다.");
                return;
            }

            try
            {
                // 1. Parse Body
                var jsonBody = await JsonEditorView.GetTextAsync();
                var message = selectedPacket.ToPacket(jsonBody);

                // 2. Parse Header
                var jsonHeader = await HeaderJsonEditorView.GetTextAsync();
                IHeader? header = null;

                if (_scriptAssembly != null)
                {
                    var headerType = _scriptAssembly.GetTypes().FirstOrDefault(t => typeof(IHeader).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                    if (headerType != null)
                    {
                        header = JsonConvert.DeserializeObject(jsonHeader, headerType) as IHeader;
                    }
                }

                if (header == null)
                {
                    throw new Exception("Failed to create Packet Header. Check script compilation or JSON format.");
                }

                // 3. Send
                await SendPacketPipelineAsync(message, header);
            }
            catch (Exception ex)
            {
                AppendLog($"[Send Error] {ex.Message}", Brushes.Red);
            }
        }

        private async Task SendPacketPipelineAsync(IMessage message, IHeader header, bool isReplay = false)
        {
            try
            {
                if (!_networkService.IsConnected)
                {
                    AppendLog("Cannot send: Disconnected", Brushes.Red);
                    return;
                }

                var packet = new Packet(header, message);
                var ctx = new PacketContext(packet, PacketDirection.Outbound);

                var interceptors = GetInterceptors(State == AppState.Replay);
                await InterceptorCall(ctx, interceptors);

                if (ctx.Drop)
                    return; // Drop?

                message = ctx.Packet.Message;

                // Encode & Send
                if (ScriptGlobals.Codec == null)
                    throw new Exception("PacketCodec not initialized.");

                var bytes = ScriptGlobals.Codec.Encode(packet);
                await _networkService.SendAsync(bytes);

                AppendLog($"[Send] {message.GetType().Name} ({bytes.Length} bytes)", Brushes.White);

                // Recording
                if (PacketRecorder.Client.IsRecording)
                    PacketRecorder.Client.Record(PacketDirection.Outbound, packet);
            }
            catch (Exception ex)
            {
                AppendLog($"[Pipeline Error] {ex}", Brushes.Red);
            }
        }

        public List<InterceptorItem> GetInterceptors(bool isReplay) => isReplay ? ReplayInterceptorSelector.GetActiveInterceptors() : ClientInterceptorSelector.GetActiveInterceptors();

        private async Task InterceptorCall(PacketContext ctx, IEnumerable<InterceptorItem> interceptors)
        {
            foreach (var interceptorItem in interceptors)
            {
                try
                {
                    var interceptor = (IPacketInterceptor)Activator.CreateInstance(interceptorItem.Type)!;

                    if (ctx.Direction == PacketDirection.Outbound)
                        await interceptor.OnOutboundAsync(ctx);
                    else
                        await interceptor.OnInboundAsync(ctx);
                }
                catch (Exception ex)
                {
                    var inner = ex;
                    while (inner.InnerException != null)
                        inner = inner.InnerException;
                    AppendLog($"[Interceptor {interceptorItem.Name}] {inner.GetType().Name}: {inner.Message}", Brushes.Red);
                    AppendLog($"  StackTrace: {inner.StackTrace}", Brushes.OrangeRed);
                }
            }
        }

        private ScriptEditorWindow? _scriptEditorWindow;

        // Replay State
        private ObservableCollection<RecordedPacket> _loadedPackets = new ObservableCollection<RecordedPacket>();

        // Workspace
        private string _workspacePath = "";
        private const string SettingsFileName = "prototesttool.settings.json";

        // UI Binding Models
        public class KeyValueItem
        {
            public string Key { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        private readonly ObservableCollection<KeyValueItem> _responseHeaders = new ObservableCollection<KeyValueItem>();

        // Services
        private readonly IReplayService _replayService = new ReplayService();

        public MainWindow(string workspacePath) : this()
        {
            _workspacePath = workspacePath;
            InitializeWorkspaceFiles(_workspacePath);
            UpdateWorkspaceUI();

            Title = $"ProtoTestTool - {_workspacePath}";

            // Load Config
            LoadWorkspaceConfiguration(_workspacePath);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) => _ = MainWindow_LoadedAsync();

        private Task MainWindow_LoadedAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_workspacePath))
                    ShowWorkspaceDialog();

                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;

                if (this.Width > screenWidth || this.Height > screenHeight)
                {
                    this.Width = Math.Min(this.Width, screenWidth * 0.9);
                    this.Height = Math.Min(this.Height, screenHeight * 0.9);
                    this.Left = (screenWidth - this.Width) / 2;
                    this.Top = (screenHeight - this.Height) / 2;
                }
            }
            catch (Exception ex)
            {
                FluentMessageBox.ShowError($"Editor Init Failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private WorkspaceConfig? _currentConfig;

        private void LoadWorkspaceConfiguration(string workspacePath)
        {
            try
            {
                _currentConfig = WorkspaceConfig.Load(workspacePath);

                // Apply Config to UI
                IpBox.Text = _currentConfig.TargetIp;
                if (PortBox != null) PortBox.Text = _currentConfig.TargetPort.ToString();
                if (ProxyLocalPortBox != null) ProxyLocalPortBox.Text = _currentConfig.ProxyLocalPort.ToString();
                if (ProxyTargetIpBox != null) ProxyTargetIpBox.Text = _currentConfig.ProxyTargetIp;
                if (ProxyTargetPortBox != null) ProxyTargetPortBox.Text = _currentConfig.ProxyTargetPort.ToString();

                var protoPath = _currentConfig.ProtoFolderPath;

                // Auto-discovery if config is empty
                if (string.IsNullOrWhiteSpace(protoPath) || !Directory.Exists(protoPath))
                {
                    try
                    {
                        if (Directory.GetFiles(workspacePath, "*.proto", SearchOption.AllDirectories).Length > 0)
                        {
                            protoPath = workspacePath;
                            _currentConfig.ProtoFolderPath = protoPath; // Update config in memory
                        }
                    }
                    catch
                    {
                    }
                }

                _protoFolderPath = protoPath ?? "";

                // Trigger Async Initialization Sequence
                _ = InitializeWorkspaceSequenceAsync(workspacePath, _protoFolderPath);
            }
            catch (Exception ex)
            {
                AppendLog($"[Config] Error loading config: {ex.Message}", Brushes.Orange);
            }
        }

        private async Task InitializeWorkspaceSequenceAsync(string workspacePath, string protoPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(protoPath) && Directory.Exists(protoPath))
                {
                    await LoadProtosFromFolderAsync(protoPath);
                }

                Dispatcher.Invoke(() => AppendLog("[Workspace] Auto-compiling scripts...", Brushes.DeepSkyBlue));
                await CompileScriptsAsync(workspacePath, (msg, brush) => Dispatcher.Invoke(() => AppendLog(msg, brush)));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[Init] Error: {ex.Message}", Brushes.Red));
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => _ = LoadHeaderJsonAsync());
            }
        }

        private void SaveWorkspaceConfiguration()
        {
            if (string.IsNullOrEmpty(_workspacePath)) 
                return;
            
            if (_currentConfig == null) 
                _currentConfig = new WorkspaceConfig();

            if (IpBox != null) 
                _currentConfig.TargetIp = IpBox.Text;
            
            if (PortBox != null && int.TryParse(PortBox.Text, out var port)) 
                _currentConfig.TargetPort = port;

            if (ProxyLocalPortBox != null && int.TryParse(ProxyLocalPortBox.Text, out var pPort)) 
                _currentConfig.ProxyLocalPort = pPort;
            
            if (ProxyTargetIpBox != null) 
                _currentConfig.ProxyTargetIp = ProxyTargetIpBox.Text;
            
            if (ProxyTargetPortBox != null && int.TryParse(ProxyTargetPortBox.Text, out var ptPort)) 
                _currentConfig.ProxyTargetPort = ptPort;

            // Save Active Interceptors
            if (ClientInterceptorSelector != null)
                _currentConfig.ActiveInterceptors["Client"] = ClientInterceptorSelector.GetActiveInterceptorNames();

            if (ProxyInterceptorSelector != null)
                _currentConfig.ActiveInterceptors["Proxy"] = ProxyInterceptorSelector.GetActiveInterceptorNames();

            if (ReplayInterceptorSelector != null)
                _currentConfig.ActiveInterceptors["Replay"] = ReplayInterceptorSelector.GetActiveInterceptorNames();

            _currentConfig.Save(_workspacePath);
        }

        #region Workspace Management

        private void WorkspaceBtn_Click(object sender, RoutedEventArgs e)
        {
            ShowWorkspaceDialog();
        }

        private void ShowWorkspaceDialog()
        {
            var dialog = new WorkspaceDialog(_workspacePath)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                var app = (App) Application.Current;
                if (!string.Equals(dialog.SelectedPath, _workspacePath, StringComparison.OrdinalIgnoreCase) &&
                    !app.TryAcquireWorkspaceLock(dialog.SelectedPath))
                {
                    MessageBox.Show(
                        "이미 다른 ProtoTestTool에서 사용 중인 워크스페이스입니다.",
                        "워크스페이스 잠금",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                _workspacePath = dialog.SelectedPath;
                SaveWorkspaceSettings();

                // Clear previous proto/script state
                _protoFolderPath = "";
                _scriptAssembly = null;
                ProtoLoaderManager.Instance.Clear();
                PacketSelectorControl.LoadPackets();

                InitializeWorkspaceFiles(_workspacePath);
                LoadWorkspaceConfiguration(_workspacePath);
                UpdateWorkspaceUI();
                AppendLog($"Workspace Loaded: {_workspacePath}", Brushes.DeepSkyBlue);
            }
            else
            {
                // If user cancels on startup (and we needed a workspace), we might want to close
                // But since this is also called from the button, we check:
                if (string.IsNullOrWhiteSpace(_workspacePath))
                {
                    Application.Current.Shutdown();
                }
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWorkspaceSettings();
            SaveWorkspaceConfiguration();

            if (ScriptGlobals.State is ScriptStateStore store)
            {
                try { store.FlushToPersistent(); }
                catch (Exception ex) { Debug.WriteLine($"[StateStore] Flush on close failed: {ex.Message}"); }
            }
        }

        private void LoadWorkspaceSettings()
        {
            try
            {
                var settingsPath = Path.Combine(GlobalSettings.AppDataDir, SettingsFileName);
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null && !string.IsNullOrWhiteSpace(settings.WorkspacePath))
                    {
                        _workspacePath = settings.WorkspacePath;
                        InitializeWorkspaceFiles(_workspacePath);

                        LoadWorkspaceConfiguration(_workspacePath);
                    }
                }
            }
            catch
            {
                // Ignore settings load errors
            }

            UpdateWorkspaceUI();
        }


        // Field to track current proto folder
        private string _protoFolderPath = "";
        private AppState State { get; set; } = AppState.None;

        private async Task LoadProtosFromFolderAsync(string folder)
        {
            await Task.Run(() =>
            {
                try
                {
                    Network.ProtoLoaderManager.Instance.LoadAllProtos(folder);
                    Dispatcher.Invoke(() =>
                    {
                        PacketSelectorControl.Refresh();
                        AppendLog($"[Proto] Compiled and Loaded from {folder}", Brushes.Green);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"[Error] Proto Compile: {ex.Message}", Brushes.Red));
                }
            });
        }

        private void SaveWorkspaceSettings()
        {
            try
            {
                Directory.CreateDirectory(GlobalSettings.AppDataDir);
                var settingsPath = Path.Combine(GlobalSettings.AppDataDir, SettingsFileName);
                var settings = new AppSettings {WorkspacePath = _workspacePath};
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, json);
            }
            catch (Exception ex)
            {
                AppendLog($"설정 저장 실패: {ex.Message}", Brushes.Red);
            }
        }

        private void UpdateWorkspaceUI()
        {
            if (!string.IsNullOrWhiteSpace(_workspacePath))
            {
                WorkspacePathText.Text = _workspacePath;
                WorkspacePathText.ToolTip = _workspacePath;
            }
            else
            {
                WorkspacePathText.Text = "(설정되지 않음)";
                WorkspacePathText.ToolTip = null;
            }
        }

        public string GetWorkspacePath() => _workspacePath;

        private class AppSettings
        {
            public string WorkspacePath { get; set; } = "";
        }

        #endregion

        #region Mode Switching

        private void ModeClient_Click(object sender, RoutedEventArgs e)
        {
            ClientView.Visibility = Visibility.Visible;
            ProxyView.Visibility = Visibility.Collapsed;
            StatusModeText.Text = "Test Client";
        }

        private void ModeProxy_Click(object sender, RoutedEventArgs e)
        {
            ClientView.Visibility = Visibility.Collapsed;
            ProxyView.Visibility = Visibility.Visible;
            StatusModeText.Text = "Proxy";
        }

        private void ResetState_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptGlobals.State is ScriptContract.ScriptStateStore store)
            {
                store.Clear();
                AppendLog("State Store Cleared.", Brushes.Gray);
            }
        }

        #endregion


        #region Script Loading & Editing

        // Moved to MainWindow.Scripting.cs

        #endregion

        #region Network Connection

        private void ClientRecordBtn_Click(object sender, RoutedEventArgs e)
        {
            HandleRecordClick(ClientRecordBtn, PacketRecorder.Client);
        }

        private void ProxyRecordBtn_Click(object sender, RoutedEventArgs e)
        {
            HandleRecordClick(ProxyRecordBtn, PacketRecorder.Proxy);
        }

        private void HandleRecordClick(System.Windows.Controls.Primitives.ToggleButton btn, PacketRecorder recorder)
        {
            var recorderName = recorder == PacketRecorder.Client ? "Client" : "Proxy";

            // Helper to update text inside StackPanel
            void UpdateButtonText(string text)
            {
                if (btn.Content is StackPanel stack && stack.Children.Count > 1 && stack.Children[1] is TextBlock tb)
                {
                    tb.Text = text;
                }
                else
                {
                    // Fallback if structure changes
                    btn.Content = text;
                }
            }

            if (btn.IsChecked == true)
            {
                if (string.IsNullOrEmpty(_workspacePath))
                {
                    MessageBox.Show("Workspace required to record.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    btn.IsChecked = false;
                    return;
                }

                recorder.Start(_workspacePath);
                UpdateButtonText("STOP");
                btn.Foreground = Brushes.Red;
                AppendLog($"[{recorderName}] Started recording.", Brushes.Red);
            }
            else
            {
                recorder.Stop();
                UpdateButtonText("REC");
                btn.Foreground = new SolidColorBrush(Color.FromRgb(255, 85, 85));
                AppendLog($"[{recorderName}] Stopped recording.", Brushes.Gray);
            }
        }


        private void UpdateConnectionState(bool connected)
        {
            ConnectToggleBtn.IsEnabled = true; // Re-enable after op
            if (connected)
            {
                ConnectToggleBtn.Content = "Disconnect";
                ConnectToggleBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
                ConnectToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.PlugDisconnected24);
            }
            else
            {
                ConnectToggleBtn.Content = "Connect";
                ConnectToggleBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
                ConnectToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.PlugConnected24);
            }

            SendBtn.IsEnabled = connected;
        }

        #endregion

        #region Sending & Receiving

        private async Task LoadHeaderJsonAsync()
        {
            if (_scriptAssembly == null)
            {
                await HeaderJsonEditorView.SetTextAsync("{}");
                return;
            }

            var headerType = _scriptAssembly.GetTypes().FirstOrDefault(t => typeof(IHeader).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                             ?? _scriptAssembly.GetTypes().FirstOrDefault(t => t.Name == "Header");

            if (headerType != null)
            {
                // Default empty header
                var dummy = Activator.CreateInstance(headerType);
                var json = JsonConvert.SerializeObject(dummy, Formatting.Indented);
                await HeaderJsonEditorView.SetTextAsync(json);
            }
            else
            {
                await HeaderJsonEditorView.SetTextAsync("{}");
            }
        }

        private async Task<string> GetProtoSourceAsync(PacketConvertor convertor)
        {
            try
            {
                var descriptorProp = convertor.Type.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (descriptorProp?.GetValue(null) is not MessageDescriptor descriptor)
                {
                    return "// Descriptor not found";
                }

                var protoFileName = descriptor.File.Name;
                var match = _loadedProtoFiles.FirstOrDefault(path =>
                    path.EndsWith(protoFileName, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals(Path.GetFileName(protoFileName), StringComparison.OrdinalIgnoreCase));

                if (match != null && File.Exists(match))
                {
                    return await File.ReadAllTextAsync(match);
                }
                else
                {
                    return $"// Source file not found for: {protoFileName}";
                }
            }
            catch (Exception ex)
            {
                return $"// Error reading source: {ex.Message}";
            }
        }

        private async void ViewProtoSourceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (PacketSelectorControl.SelectedPacket is not PacketConvertor convertor)
            {
                FluentMessageBox.ShowError("패킷을 먼저 선택해 주세요.");
                return;
            }

            var source = await GetProtoSourceAsync(convertor);
            var window = new SourceViewerWindow($"Proto Source: {convertor.Name}", source, "proto");
            window.Owner = this;
            window.Show();
        }

        private async void SendBtn_Click(object sender, RoutedEventArgs e)
        {
            await SendBtn_ClickAsync(sender, e);
        }


        public void SendPacket(IHeader header, IMessage message)
        {
            _ = SendPacketPipelineAsync(message, header, isReplay: false);
        }


        private void OnDataReceived(byte[] data)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                _receiveBuffer.Append(data);
                await ProcessReceiveBuffer();
            });
        }

        private async Task ProcessReceiveBuffer()
        {
            var codec = ScriptGlobals.Codec;
            if (codec == null) return;

            while (_receiveBuffer.Length > 0)
            {
                try
                {
                    var span = _receiveBuffer.WrittenSpan;
                    var readSize = codec.TryDecode(ref span, out var packet);
                    if (readSize <= 0)
                        break;

                    Debug.Assert(packet != null);
                    _receiveBuffer.Consume(readSize);

                    if (PacketRecorder.Client.IsRecording)
                        PacketRecorder.Client.Record(PacketDirection.Inbound, packet);

                    var interceptors = GetInterceptors(State == AppState.Replay);
                    await InterceptorCall(new PacketContext(packet, PacketDirection.Inbound), interceptors);

                    var jsonBody = JsonConvert.SerializeObject(packet.Message, Formatting.Indented);
                    var header = packet.Header.ToJsonString();

                    AppendLog($"[Recv] {packet.Message.GetType().Name} ({readSize} bytes)", Brushes.LimeGreen);

                    var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(header);
                    _responseHeaders.Clear();

                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                            _responseHeaders.Add(new KeyValueItem { Key = kvp.Key, Value = kvp.Value?.ToString() ?? "" });
                    }

                    await ResponseBoxView.SetTextAsync(jsonBody);
                }
                catch (Exception ex)
                {
                    AppendLog($"Decoder Error: {ex.Message}", Brushes.Red);
                    _receiveBuffer.Clear();
                    break;
                }
            }
        }

        #endregion


        internal void AppendLog(string message, Brush? color = null)
        {
            var paragraph = new Paragraph(new Run(message))
            {
                Foreground = color ?? Brushes.LightGray,
                Margin = new Thickness(0)
            };
            LogBox.Document.Blocks.Add(paragraph);
            LogBox.ScrollToEnd();
        }

        private void ScriptListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Placeholder for script selection logic
        }

        private void OpenScriptEditor_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_workspacePath))
            {
                MessageBox.Show("Please create or open a workspace first.", "Workspace Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_scriptEditorWindow == null || !_scriptEditorWindow.IsLoaded)
            {
                _scriptEditorWindow = new ScriptEditorWindow(_workspacePath, _scriptLoader);
                _scriptEditorWindow.Closed += (s, args) => _scriptEditorWindow = null;

                _scriptEditorWindow.OnRequestCompilation += () =>
                {
                    // Trigger compilation on Main Thread? 
                    // CompileScriptsAsync is async.
                    Dispatcher.Invoke(async () => { await CompileScriptsAsync(_workspacePath, _scriptEditorWindow.AppendLog); });
                };

                _scriptEditorWindow.Show();
            }
            else
            {
                _scriptEditorWindow.Activate();
                if (_scriptEditorWindow.WindowState == WindowState.Minimized)
                    _scriptEditorWindow.WindowState = WindowState.Normal;
            }
        }

        private async void LoadRecordingBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSONP Files (*.jsonp)|*.jsonp|All Files (*.*)|*.*",
                InitialDirectory = Path.Combine(_workspacePath, "Recordings")
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // Delegated to ReplayService
                    var packets = await _replayService.LoadRecordingAsync(dialog.FileName);
                    if (packets.Count > 0)
                    {
                        _loadedPackets = new ObservableCollection<RecordedPacket>(packets);
                        ReplayPacketGrid.ItemsSource = _loadedPackets;
                        ReplayFileNameText.Text = Path.GetFileName(dialog.FileName);
                        AppendLog($"Loaded {packets.Count} packets from {Path.GetFileName(dialog.FileName)}", Brushes.Green);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load recording: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void ReplayAllBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedPackets.Count == 0)
            {
                MessageBox.Show("No packets loaded.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }


            if (!_networkService.IsConnected)
            {
                MessageBox.Show("Not connected to server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            State = AppState.Replay;

            // Delegate to ReplayService
            await _replayService.ReplayAllAsync(_loadedPackets.ToList(), async (msg, headerObj) =>
            {
                // Resolve Dynamic Header Type from ScriptAssembly
                Type? headerType = null;
                if (_scriptAssembly != null)
                {
                    headerType = _scriptAssembly.GetTypes().FirstOrDefault(t => typeof(IHeader).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                }

                IHeader? header = null;
                // Header Conversion (JsonElement -> Dynamic Header Type)
                if (headerType != null && headerObj is System.Text.Json.JsonElement je)
                {
                    try
                    {
                        header = (IHeader?) JsonConvert.DeserializeObject(je.GetRawText(), headerType);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Replay] Header parse failed: {ex.Message}");
                    }
                }

                if (header == null)
                    return;

                await SendPacketPipelineAsync(msg, header, isReplay: true);
            }, AppendLog);

            State = AppState.None;

            AppendLog("Replay Finished.", Brushes.Green);
        }

        public enum AppState
        {
            None,
            Replay,
        }

        private async void ReplayPacketGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadedPackets == null) return;

            if (ReplayPacketGrid.SelectedIndex >= 0 && ReplayPacketGrid.SelectedIndex < _loadedPackets.Count)
            {
                var packet = _loadedPackets[ReplayPacketGrid.SelectedIndex];

                // 1. Display Full Packet Details (Body Tab)
                if (packet != null)
                {
                    try
                    {
                        // Use System.Text.Json to correctly handle JsonElement
                        var options = new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        };
                        var content = System.Text.Json.JsonSerializer.Serialize(packet, options);

                        await ReplayBodyJsonViewer.SetTextAsync(content);
                    }
                    catch (Exception ex)
                    {
                        await ReplayBodyJsonViewer.SetTextAsync($"// Error: {ex.Message}");
                    }
                }
                else
                {
                    await ReplayBodyJsonViewer.SetTextAsync("");
                }

                // 2. Display Header
                if (packet?.Header != null)
                {
                    try
                    {
                        var headerJson = System.Text.Json.JsonSerializer.Serialize(packet.Header, new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                        await ReplayHeaderJsonViewer.SetTextAsync(headerJson);
                    }
                    catch
                    {
                        await ReplayHeaderJsonViewer.SetTextAsync("// Error displaying header");
                    }
                }
                else
                {
                    await ReplayHeaderJsonViewer.SetTextAsync("");
                }
            }
        }
    }
}