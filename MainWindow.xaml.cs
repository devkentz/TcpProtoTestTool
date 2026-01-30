using ProtoTestTool.Services;
using System.Windows;


using System.Windows.Documents;
using System.Collections.Generic;
using System.Windows.Media;
using System.IO;
using Newtonsoft.Json;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;
using System.Collections.ObjectModel;
using Google.Protobuf;
using Google.Protobuf.Reflection;

using System.Windows.Controls;
using Microsoft.Win32; // For OpenFileDialog


using System.Text.Encodings.Web;

namespace ProtoTestTool
{

    public partial class MainWindow
    {
        private INetworkService _networkService; // Use Interface
        private readonly ScriptLoader _scriptLoader = new();
        private readonly List<byte> _receiveBuffer = new();
        // ... (other fields)

        // ...

        public MainWindow()
        {
            InitializeComponent();
            _networkService = new NetworkService(); // Manual DI for now

            // Subscribe to events globally or in Connect method? 
            // Better to subscribe once here if life-cycle matches window.
            _networkService.Connected += OnConnected;
            _networkService.Disconnected += OnDisconnected;
            _networkService.ErrorOccurred += OnError;
            _networkService.DataReceived += OnDataReceived;

            Closing += MainWindow_Closing;
            // ...
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

        // ...

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

        // ...

        private async void PacketSelectorControl_PacketSelected(object sender, RoutedEventArgs e)
        {
            // ...
                // Check service status
                SendBtn.IsEnabled = _networkService.IsConnected;
            // ...
        }

        // ...

        private async Task SendBtn_ClickAsync(object sender, RoutedEventArgs e)
        {
             // ...
                if (!_networkService.IsConnected)
                {
                    FluentMessageBox.ShowError("서버에 연결되지 않았습니다.");
                    return;
                }
             // ...
        }

        private Task SendPacketPipelineAsync(IMessage message, IHeader headerObj, bool isReplay = false)
        {
            try
            {
                if (!_networkService.IsConnected)
                {
                    AppendLog("Cannot send: Disconnected", Brushes.Red);
                    return Task.CompletedTask;
                }
                
                // ... (Interceptor logic)

                var packet = new Packet(headerObj, message);

                // Encode & Send
                var bytes = ScriptGlobals.Codec.Encode(packet);
                _networkService.SendAsync(bytes); // Fire and forget wrapper

                AppendLog($"[Send] {message.GetType().Name} ({bytes.Length} bytes)", Brushes.White);
                
                // ... (Recording logic)
            }
            catch (Exception ex)
            {
                AppendLog($"[Pipeline Error] {ex.Message}", Brushes.Red);
            }
            return Task.CompletedTask;
        }

        // Roslyn
        // private readonly RoslynService _roslynService;
        private ScriptEditorWindow? _scriptEditorWindow;

        // Editor State
        private string _currentEditingFile = "";
        
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

        public class HeaderScriptGlobals
        {
            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        }

        private ObservableCollection<KeyValueItem> _requestHeaders = new ObservableCollection<KeyValueItem>(); // Kept for compilation safety until removed
        private ObservableCollection<KeyValueItem> _responseHeaders = new ObservableCollection<KeyValueItem>();

        // Services
        private readonly IReplayService _replayService = new ReplayService();

        public MainWindow(string workspacePath) : this()
        {
            _workspacePath = workspacePath;
            InitializeWorkspaceFiles(_workspacePath);
            // Monaco Editors initialize themselves via Loaded event.


            Title = $"ProtoTestTool - {_workspacePath}";

            string settingsPath = Path.Combine(_workspacePath, "settings.json");
            if (File.Exists(settingsPath))
            {
                LoadWorkspaceSettings();
            }
            
            // Re-compile logic
            _ = Dispatcher.InvokeAsync(async () => 
            {
                 // Small delay to ensure UI ready
                 await Task.Delay(500);
                 await LoadProtosFromFolderAsync(_workspacePath);
                 await CompileWorkspaceScriptsAsync(_workspacePath);
            });
        }

        public MainWindow()
        {
            InitializeComponent();
            Closing += MainWindow_Closing;

            // Initialize Globals with dummy logger for startup (real logger injected on compilation)
            ScriptGlobals.Initialize(
                new ScriptStateStore(),
                new ToolScriptLogger((msg, color) =>
                {
                    /* Startup Log */
                })
            );

            // _roslynService = new RoslynService(); (Removed)

            // Load Workspace Settings (Will be overridden if constructor arg is provided)
            LoadWorkspaceSettings();

            // Use Loaded event for async initialization (safer than constructor)
            Loaded += MainWindow_Loaded;

            // Load existing Protos
            Network.ProtoLoaderManager.Instance.LoadAllProtos();
            // PacketSelectorControl handles its own loading
            // PacketListBox.ItemsSource = Network.ProtoLoaderManager.Instance.SendPackets.Values;

            // Bind Headers
            // RequestHeaderGrid removed (replaced by HeaderScriptEditor)
            ResponseHeaderGrid.ItemsSource = _responseHeaders;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) => _ = MainWindow_LoadedAsync();

        private async Task MainWindow_LoadedAsync()
        {
            try
            {
                // Initialize Roslyn Editor (Removed for Monaco migration)
                // if (_roslynService?.Host != null)
                // {
                //    ProtoSourceViewer is now a TextBox
                // }
                // Show Workspace dialog if not set (Handled by App.xaml.cs, but fallback if empty?)
                if (string.IsNullOrEmpty(_workspacePath))
                {
                    ShowWorkspaceDialog();
                }

                // Resolution-Aware Resizing
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;

                if (this.Width > screenWidth || this.Height > screenHeight)
                {
                    this.Width = Math.Min(this.Width, screenWidth * 0.9);
                    this.Height = Math.Min(this.Height, screenHeight * 0.9);
                    this.Left = (screenWidth - this.Width) / 2;
                    this.Top = (screenHeight - this.Height) / 2;
                }


                // Initialize Monaco Editors handled by control
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                FluentMessageBox.ShowError($"Editor Init Failed: {ex.Message}");
            }
        }

        // Legacy Monaco Init Logic Removed


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
                var app = (App)Application.Current;
                if (!app.TryAcquireWorkspaceLock(dialog.SelectedPath))
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
            SaveWorkspaceSettings(); // Global Recent List
            SaveWorkspaceConfiguration(); // Current Workspace Config
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

        private void LoadWorkspaceConfiguration(string path)
        {
            var config = WorkspaceConfig.Load(path);

            // Connection
            IpBox.Text = config.TargetIp;
            PortBox.Text = config.TargetPort.ToString();

            // Proxy
            ProxyLocalPortBox.Text = config.ProxyLocalPort.ToString();
            ProxyTargetIpBox.Text = config.ProxyTargetIp;
            ProxyTargetPortBox.Text = config.ProxyTargetPort.ToString();

            // Proto Path - Load protos first
            var protoPath = config.ProtoFolderPath;

            // Auto-discovery if config is empty
            if (string.IsNullOrWhiteSpace(protoPath) || !Directory.Exists(protoPath))
            {
                try
                {
                    if (Directory.GetFiles(path, "*.proto", SearchOption.AllDirectories).Length > 0)
                    {
                        protoPath = path;
                        // Optional: Update config automatically?
                        // config.ProtoFolderPath = path;
                        // config.Save(path);
                    }
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(protoPath) && Directory.Exists(protoPath))
            {
                _protoFolderPath = protoPath;
                // Asynchronously load protos and then compile scripts
                _ = LoadWorkspaceAsync(path, protoPath);
            }
            else
            {
                // No proto folder, just compile scripts
                _ = CompileWorkspaceScriptsAsync(path);
            }
        }

        private async Task LoadWorkspaceAsync(string workspacePath, string protoFolderPath)
        {
            try
            {
                // 1. Load Proto files first
                await LoadProtosFromFolderAsync(protoFolderPath);

                // 2. Then compile CSX scripts
                await CompileWorkspaceScriptsAsync(workspacePath);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[Error] Workspace load failed: {ex.Message}", Brushes.Red));
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => _ = LoadHeaderJsonAsync());
            }
        }

        private async Task CompileWorkspaceScriptsAsync(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return;

            // Check if required script files exist
            // Delegate validation to CompileScriptsAsync
            try
            {
                Dispatcher.Invoke(() => AppendLog("[Workspace] Auto-compiling scripts...", Brushes.DeepSkyBlue));
                await CompileScriptsAsync(workspacePath, (msg, color) => Dispatcher.Invoke(() => AppendLog(msg, color)));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[Error] Auto-compile failed: {ex.Message}", Brushes.Red));
            }
        }

        private void SaveWorkspaceConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_workspacePath) || !Directory.Exists(_workspacePath)) return;

            var config = new WorkspaceConfig
            {
                TargetIp = IpBox.Text,
                ProtoFolderPath = _protoFolderPath,
                ProxyTargetIp = ProxyTargetIpBox.Text
            };

            if (int.TryParse(PortBox.Text, out var port)) config.TargetPort = port;
            if (int.TryParse(ProxyLocalPortBox.Text, out var pLocal)) config.ProxyLocalPort = pLocal;
            if (int.TryParse(ProxyTargetPortBox.Text, out var pTarget)) config.ProxyTargetPort = pTarget;

            config.Save(_workspacePath);
        }

        // Field to track current proto folder
        private string _protoFolderPath = "";

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
        }

        private void ModeProxy_Click(object sender, RoutedEventArgs e)
        {
            ClientView.Visibility = Visibility.Collapsed;
            ProxyView.Visibility = Visibility.Visible;
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

        private void ConnectToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null && _client.IsConnected)
            {
                // Disconnect
                _client.DisconnectAndStop();
            }
            else
            {
                // Connect
                var ip = IpBox.Text;
                if (!int.TryParse(PortBox.Text, out var port))
                {
                    AppendLog("Invalid Port", Brushes.Red);
                    return;
                }

                SaveWorkspaceConfiguration();

                // Disable button while connecting
                ConnectToggleBtn.IsEnabled = false;

                try
                {
                    if (_client != null)
                    {
                        _client.DisconnectAndStop();
                        _client = null;
                    }

                    _client = new SimpleTcpClient(ip, port);
                    _client.Connected += () => Dispatcher.Invoke(() =>
                    {
                        AppendLog($"Connected to {ip}:{port}", Brushes.DeepSkyBlue);
                        UpdateConnectionState(true);
                    });
                    _client.Disconnected += () => Dispatcher.Invoke(() =>
                    {
                        AppendLog("Disconnected", Brushes.Orange);
                        UpdateConnectionState(false);
                    });
                    _client.DataReceived += OnDataReceived;
                    _client.ErrorOccurred += (err) => Dispatcher.Invoke(() =>
                    {
                        AppendLog($"Socket Error: {err}", Brushes.Red);
                        UpdateConnectionState(false); // Ensure button resets on error
                    });

                    _client.ConnectAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"Connection failed: {ex.Message}", Brushes.Red);
                    UpdateConnectionState(false);
                }
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

        private async void PacketSelectorControl_PacketSelected(object sender, RoutedEventArgs e)
        {
            try
            {
                if (PacketSelectorControl.SelectedPacket is not PacketConvertor convertor) return;

                // Generate Default JSON
                var (_, json) = convertor.DefaultJsonString();
                await JsonEditorView.SetTextAsync(json);
                SendBtn.IsEnabled = _client != null && _client.IsConnected;

                _currentEditingFile = convertor.Name;

                // Generate Default JSON for Header
                await LoadHeaderJsonAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] Selection changed: {ex.Message}", Brushes.Red);
            }
        }

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

        private async Task SendBtn_ClickAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                if (PacketSelectorControl.SelectedPacket == null)
                {
                    AppendLog("No packet type selected.", Brushes.Red);
                    return;
                }

                if (_client == null || !_client.IsConnected)
                {
                    FluentMessageBox.ShowError("서버에 연결되지 않았습니다.");
                    return;
                }

                var type = (PacketSelectorControl.SelectedPacket as PacketConvertor)?.Type;
                if (type == null)
                {
                    AppendLog("No packet type selected.", Brushes.Red);
                    return;
                }

                var json = await JsonEditorView.GetTextAsync();

                if (JsonConvert.DeserializeObject(json, type) is not IMessage message)
                    return;

                // Client Interceptor Hook
                if (_clientInterceptor != null)
                {
                    var ctx = new ClientPacketContext(message);


                    _clientInterceptor.OnBeforeSend(ctx);
                    message = ctx.Message;
                }

                // Find Header Type
                Type? headerType = null;
                // MainWindow.Scripting.cs should expose _headerAssembly or we access it via reflection/event
                // But _headerAssembly is private in MainWindow block.
                // We are in the same partial class 'MainWindow'.
                // verifying _headerAssembly visibility. It was added as 'private' in MainWindow.Scripting.cs (partial).
                // Private fields in partial classes are shared across files.

                if (_scriptAssembly != null)
                {
                    headerType = _scriptAssembly.GetTypes().FirstOrDefault(t => typeof(IHeader).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                    if (headerType == null) headerType = _scriptAssembly.GetTypes().FirstOrDefault(t => t.Name == "Header");
                }

                if (headerType == null)
                {
                    AppendLog("Header class not found in compiled scripts.", Brushes.Red);
                    return;
                }

                // Header JSON to Object
                IHeader? headerObj = null;
                try
                {
                    // Use HeaderJsonEditor.Text
                    headerObj = (IHeader?) JsonConvert.DeserializeObject(await HeaderJsonEditorView.GetTextAsync(), headerType);
                }
                catch (Exception ex)
                {
                    AppendLog($"Header JSON Error: {ex.Message}", Brushes.Red);
                    return;
                }

                if (headerObj == null)
                {
                    AppendLog("Header object is null.", Brushes.Red);
                    return;
                }

                await SendPacketPipelineAsync(message, headerObj);
            }
            catch (Exception ex)
            {
                FluentMessageBox.ShowError($"전송 오류: {ex.Message}");
                AppendLog($"[Error] {ex.Message}", Brushes.Red);
            }
        }

        private Task SendPacketPipelineAsync(IMessage message, IHeader headerObj, bool isReplay = false)
        {
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    AppendLog("Cannot send: Disconnected", Brushes.Red);
                    return Task.CompletedTask;
                }

                // Client Interceptor Hook
                if (_clientInterceptor != null)
                {
                    var ctx = new ClientPacketContext(message);
                    
                    _clientInterceptor.OnBeforeSend(ctx);
                    message = ctx.Message;
                }

                var packet = new Packet(headerObj, message);

                // Encode & Send
                var bytes = ScriptGlobals.Codec.Encode(packet);
                _client.SendAsync(bytes.Span);

                AppendLog($"[Send] {message.GetType().Name} ({bytes.Length} bytes)", Brushes.White);

                if (PacketRecorder.Client.IsRecording && !isReplay)
                {
                    PacketRecorder.Client.Record(PacketDirection.Outbound, packet);
                }
            }

            catch (Exception ex)
            {
                AppendLog($"[Pipeline Error] {ex.Message}", Brushes.Red);
            }
            return Task.CompletedTask;
        }

        public void SendPacket(IHeader header, IMessage message)
        {
            if (_client == null || !_client.IsConnected)
            {
                AppendLog("Not connected.", Brushes.Red);
                return;
            }

            if (ScriptGlobals.Codec == null)
            {
                AppendLog("Codec not initialized (Compile first).", Brushes.Red);
                return;
            }

            try
            {
                // Client Interceptor Hook
                if (_clientInterceptor != null)
                {
                    var ctx = new ClientPacketContext(message);
                    _clientInterceptor.OnBeforeSend(ctx);
                    message = ctx.Message;
                }

                var bytes = ScriptGlobals.Codec.Encode(new Packet(header, message));
                _client.SendAsync(bytes.ToArray());
                AppendLog($"[Send] {message.GetType().Name} ({bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                AppendLog($"Send Error: {ex.Message}", Brushes.Red);
            }
        }


        private void OnDataReceived(byte[] data)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                _receiveBuffer.AddRange(data);
                await ProcessReceiveBuffer();
            });
        }

        private async Task ProcessReceiveBuffer()
        {
            if (ScriptGlobals.Codec == null) return;

            // Inefficient List -> Array -> ROS loop for prototype
            while (_receiveBuffer.Count > 0)
            {
                var currentBytes = _receiveBuffer.ToArray();
                ReadOnlySpan<byte> span = currentBytes.AsSpan();

                try
                {
                    var readSize = ScriptGlobals.Codec.TryDecode(ref span, out var packet);
                    if (readSize > 0)
                    {
                        if (PacketRecorder.Client.IsRecording && packet != null)
                        {
                            PacketRecorder.Client.Record(PacketDirection.Inbound, packet);
                        }

                        _receiveBuffer.RemoveRange(0, (int) readSize);

                        if (packet != null)
                        {
                            var jsonBody = JsonConvert.SerializeObject(packet.Message, Formatting.Indented);
                            var header = packet.Header.ToJsonString();
                            AppendLog($"[Recv] {packet.Message.GetType().Name} ({readSize} bytes)", Brushes.LimeGreen);
                            // Update Response Inspector (Headers)
                            try
                            {
                                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(header);
                                _responseHeaders.Clear();
                                if (dict != null)
                                {
                                    foreach (var kvp in dict)
                                    {
                                        _responseHeaders.Add(new KeyValueItem {Key = kvp.Key, Value = kvp.Value?.ToString() ?? ""});
                                    }
                                }
                            }
                            catch
                            {
                            }

                            // Update Response Inspector (Body)
                            await ResponseBoxView.SetTextAsync(jsonBody);
                        }
                    }
                    else
                    {
                        break; // Need more data
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Decoder Error: {ex.Message}", Brushes.Red);
                    // Prevent infinite loop on bad data
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
                    
                    if (packets != null)
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
            if (_loadedPackets == null || _loadedPackets.Count == 0)
            {
                MessageBox.Show("No packets loaded.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }



            if (_client == null || !_client.IsConnected)
            {
                 MessageBox.Show("Not connected to server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                 return;
            }

            // Delegate to ReplayService
            await _replayService.ReplayAllAsync(_loadedPackets.ToList(), async (msg, headerObj) => 
            {
                 // Resolve Dynamic Header Type from ScriptAssembly
                 Type? headerType = null;
                 if (_scriptAssembly != null)
                 {
                     headerType = _scriptAssembly.GetTypes().FirstOrDefault(t => typeof(IHeader).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                     if (headerType == null) headerType = _scriptAssembly.GetTypes().FirstOrDefault(t => t.Name == "Header");
                 }

                 IHeader? header = null;
                 // Header Conversion (JsonElement -> Dynamic Header Type)
                 if (headerType != null && headerObj is System.Text.Json.JsonElement je)
                 {
                     try 
                     {
                         header = (IHeader?)JsonConvert.DeserializeObject(je.GetRawText(), headerType);
                     }
                     catch { /* Header parse fail */ }
                 }
                 
                 // Fallback or if headerType null (shouldn't happen if connected properly)
                 if (header == null) 
                 {
                     // Without a valid header, we cannot send the packet reliably through the pipeline
                     // as the pipeline expects a concrete header type (usually).
                     // Log and skip.
                     // AppendLog($"[Replay Error] Header resolution failed for {msg.Descriptor.Name}", Brushes.Red); 
                     // We can try to send with null if pipeline supports it, but SendPacketPipelineAsync signature usually requires non-null.
                     // Let's check SendPacketPipelineAsync signature. 
                     // It is defined as: SendPacketPipelineAsync(IMessage message, IHeader header, bool isReplay = false) (inferred)
                     
                     // If we really want to support 'untyped' replays, we'd need a LooseHeader or similar.
                     // For now, strict behavior matches original logic.
                      return; 
                 }

                 await SendPacketPipelineAsync(msg, header, isReplay: true);

            }, AppendLog);
            
            AppendLog("Replay Finished.", Brushes.Green);
        }

        private async void ReplayPacketGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // PROBE: Verify Event Firing
            // MessageBox.Show($"Selection Changed! Index: {ReplayPacketGrid.SelectedIndex}");

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