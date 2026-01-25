using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.IO;
using Newtonsoft.Json;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;
using System.Collections.ObjectModel;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Windows.Controls;


namespace ProtoTestTool
{
    public partial class MainWindow : Wpf.Ui.Controls.UiWindow
    {
        private SimpleTcpClient? _client;
        private readonly ScriptLoader _scriptLoader = new();
        private readonly List<byte> _receiveBuffer = new();

        // Roslyn
        // private readonly RoslynService _roslynService;
        private ScriptEditorWindow? _scriptEditorWindow;

        // Editor State
        private string _currentEditingFile = "";

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

        public MainWindow()
        {
            InitializeComponent();
            Closing += MainWindow_Closing;

            // Initialize Globals with dummy logger for startup (real logger injected on compilation)
            ScriptGlobals.Initialize(
                new ScriptStateStore(),
                new ToolScriptLogger((msg, color) => { /* Startup Log */ })
            );

            // _roslynService = new RoslynService(); (Removed)

            // Load Workspace Settings
            LoadWorkspaceSettings();

            // Use Loaded event for async initialization (safer than constructor)
            Loaded += MainWindow_Loaded;

            // Load existing Protos
            Network.ProtoLoaderManager.Instance.LoadAllProtos();
            PacketListBox.ItemsSource = Network.ProtoLoaderManager.Instance.SendPackets.Values;

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
                // Show Workspace dialog if not set
                // Always show workspace dialog on startup as requested
                ShowWorkspaceDialog();

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



                // Initialize Monaco Editors
                await InitializeMonacoEditors();
            }
            catch (Exception ex)
            {
                FluentMessageBox.ShowError($"Editor Init Failed: {ex.Message}");
            }
        }

        private async Task InitializeMonacoEditors()
        {
            var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
            if (!File.Exists(editorPath))
            {
                MessageBox.Show($"Editor file not found: {editorPath}");
                return;
            }

            var uri = new Uri(editorPath).AbsoluteUri;

            await InitializeSingleEditor(JsonEditorView, uri, "json");
            await InitializeSingleEditor(HeaderJsonEditorView, uri, "json");
            await InitializeSingleEditor(ProtoSourceView, uri, "proto");
            await InitializeSingleEditor(ResponseBoxView, uri, "json");
        }

        private async Task InitializeSingleEditor(Microsoft.Web.WebView2.Wpf.WebView2 webView, string uri, string language)
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.CoreWebView2.Navigate(uri);
            
            // Wait for ready (in a real scenario we might wait for the message, 
            // but for now a small delay + initial setup script is okay)
            webView.CoreWebView2.WebMessageReceived += (s, args) =>
            {
               // dynamic msg = JsonConvert.DeserializeObject(args.TryGetWebMessageAsString());
               // if (msg.type == "ready") { ... }
            };

            webView.NavigationCompleted += async (s, e) => 
            {
                if (e.IsSuccess)
                {
                   await webView.ExecuteScriptAsync($"setLanguage('{language}');");
                   await webView.ExecuteScriptAsync("setTheme('vs-dark');");
                }
            };
        }

        private async Task SetEditorContentAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string content)
        {
            if (webView?.CoreWebView2 == null) return;
            var jsonContent = JsonConvert.SerializeObject(content);
            await webView.ExecuteScriptAsync($"setContent({jsonContent});");
        }

        private async Task<string> GetEditorContentAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView)
        {
            if (webView?.CoreWebView2 == null) return string.Empty;
            var result = await webView.ExecuteScriptAsync("getContent();");
            // Result is JSON encoded string (e.g. "\"content\""), need to unquote
            if (result == "null" || result == "undefined") return string.Empty;
             return JsonConvert.DeserializeObject<string>(result) ?? string.Empty;
        }

        #region Workspace Management
        private void ManagePackagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_workspacePath))
            {
                FluentMessageBox.ShowError("Please open a workspace first.");
                return;
            }
            var win = new NuGetWindow(_workspacePath);
            win.Owner = this;
            win.ShowDialog();
            
            // Recompile to pick up new packages? 
            // Optional: _ = CompileScriptsAsync(false);
        }

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
                _workspacePath = dialog.SelectedPath;
                // Save workspace to Global Settings is done inside Dialog.SelectWorkspace
                // But we still persist specific settings if needed:
                SaveWorkspaceSettings(); 
                
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
            SaveWorkspaceSettings();       // Global Recent List
            SaveWorkspaceConfiguration();  // Current Workspace Config
        }

        private void LoadWorkspaceSettings()
        {
            try
            {
                var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
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
                catch {}
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
        }

        private async Task CompileWorkspaceScriptsAsync(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return;

            // Check if required script files exist
            // Check if required script files exist
            var registryPath = Path.Combine(workspacePath, "PacketRegistry.cs");
            if (!File.Exists(registryPath)) registryPath = Path.Combine(workspacePath, "PacketRegistry.csx");
            
            var serializerPath = Path.Combine(workspacePath, "PacketSerializer.cs");
            if (!File.Exists(serializerPath)) serializerPath = Path.Combine(workspacePath, "PacketSerializer.csx");

            if (!File.Exists(registryPath) || !File.Exists(serializerPath))
            {
                Dispatcher.Invoke(() => AppendLog("[Info] Required CSX files not found. Skipping auto-compile.", Brushes.Gray));
                return;
            }

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
             try
             {
                 var files = Directory.GetFiles(folder, "*.proto", SearchOption.AllDirectories);
                 if (files.Length > 0)
                 {
                      await ProcessProtosAsync(files);
                      Dispatcher.Invoke(() => AppendLog($"[Proto] Auto-loaded from {folder}", Brushes.Green));
                 }
             }
             catch (Exception ex)
             {
                 Dispatcher.Invoke(() => AppendLog($"[Error] Auto-load proto: {ex.Message}", Brushes.Red));
             }
        }

        private void SaveWorkspaceSettings()
        {
            try
            {
                var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                var settings = new AppSettings { WorkspacePath = _workspacePath };
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

        private void ModeEditor_Click(object sender, RoutedEventArgs e)
        {
            // Removed: EditorView.Visibility = Visibility.Visible;
        }
        
        private void ResetState_Click(object sender, RoutedEventArgs e)
        {
            // ScriptGlobals.Initialize(new ScriptStateStore(), ...);
            // Or just clear.
            if (ScriptContract.ScriptGlobals.State is ScriptContract.ScriptStateStore store)
            {
                store.Clear();
                AppendLog("State Store Cleared.");
            }
        }
        #endregion


        


        #region Script Loading & Editing
        // Moved to MainWindow.Scripting.cs
        #endregion

        #region Network Connection
        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            var ip = IpBox.Text;
            if (!int.TryParse(PortBox.Text, out var port))
            {
                AppendLog("Invalid Port", Brushes.Red);
                return;
            }

            SaveWorkspaceConfiguration();

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
                _client.ErrorOccurred += (err) => Dispatcher.Invoke(() => AppendLog($"Socket Error: {err}", Brushes.Red));

                _client.ConnectAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"Connection failed: {ex.Message}", Brushes.Red);
            }
        }

        private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _client?.DisconnectAndStop();
        }

        private void OpenScriptEditor_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_workspacePath)) 
            {
                AppendLog("Please load a workspace first.", Brushes.Red);
                return;
            }

            if (_scriptEditorWindow == null || !_scriptEditorWindow.IsLoaded)
            {
                _scriptEditorWindow = new ScriptEditorWindow(_workspacePath, _scriptLoader);
                
                _scriptEditorWindow.OnRequestCompilation += () => 
                {
                    var win = _scriptEditorWindow;
                    if (win == null) return;
                    
                    Action<string, Brush> editorLogger = (msg, color) => 
                    {
                        win.Dispatcher.Invoke(() => win.AppendLog(msg, color));
                    };

                    Dispatcher.InvokeAsync(async () => 
                    {
                        await CompileScriptsAsync(_workspacePath, editorLogger);
                    });
                };
                
                _scriptEditorWindow.Show();
            }
            else
            {
                _scriptEditorWindow.Activate();
                if (_scriptEditorWindow.WindowState == WindowState.Minimized) _scriptEditorWindow.WindowState = WindowState.Normal;
            }
        }

        private void ScriptListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                OpenScriptEditor_Click(sender, e);
                // Keeping selection is fine as visual feedback of "last clicked"
            }
        }

        private void UpdateConnectionState(bool connected)
        {
            ConnectBtn.IsEnabled = !connected;
            DisconnectBtn.IsEnabled = connected;
            SendBtn.IsEnabled = connected;
        }
        #endregion

        #region Sending & Receiving
        private async void PacketListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                if (PacketListBox.SelectedItem is not PacketConvertor convertor) return;

                // Generate Default JSON
                var (_, json) = convertor.DefaultJsonString();
                await SetEditorContentAsync(JsonEditorView, json);
                SendBtn.IsEnabled = _client != null && _client.IsConnected;

                _currentEditingFile = convertor.Name;

                // Generate Default JSON for Header
                await LoadHeaderJsonAsync();

                // Display Proto Source
                await LoadProtoSourceAsync(convertor);
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] Selection changed: {ex.Message}", Brushes.Red);
            }
        }

        private async Task LoadHeaderJsonAsync()
        {
            if (_headerAssembly == null)
            {
                await SetEditorContentAsync(HeaderJsonEditorView, "{}");
                return;
            }

            var headerType = _headerAssembly.GetTypes().FirstOrDefault(t => typeof(IHeader).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                          ?? _headerAssembly.GetTypes().FirstOrDefault(t => t.Name == "Header");

            if (headerType == null)
            {
                await SetEditorContentAsync(HeaderJsonEditorView, "{}");
                return;
            }

            try
            {
                var headerInstance = Activator.CreateInstance(headerType);
                var headerJson = JsonConvert.SerializeObject(headerInstance, Formatting.Indented);
                await SetEditorContentAsync(HeaderJsonEditorView, headerJson);
            }
            catch
            {
                await SetEditorContentAsync(HeaderJsonEditorView, "{}");
            }
        }

        private async Task LoadProtoSourceAsync(PacketConvertor convertor)
        {
            try
            {
                var descriptorProp = convertor.Type.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (descriptorProp?.GetValue(null) is not MessageDescriptor descriptor)
                {
                    await SetEditorContentAsync(ProtoSourceView, "// Descriptor not found");
                    return;
                }

                var protoFileName = descriptor.File.Name;
                var match = _loadedProtoFiles.FirstOrDefault(path =>
                    path.EndsWith(protoFileName, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals(Path.GetFileName(protoFileName), StringComparison.OrdinalIgnoreCase));

                if (match != null && File.Exists(match))
                {
                    await SetEditorContentAsync(ProtoSourceView, await File.ReadAllTextAsync(match));
                }
                else
                {
                    await SetEditorContentAsync(ProtoSourceView, $"// Source file not found for: {protoFileName}");
                }
            }
            catch (Exception ex)
            {
                await SetEditorContentAsync(ProtoSourceView, $"// Error loading source: {ex.Message}");
            }
        }
        
        private async void SendBtn_Click(object sender, RoutedEventArgs e)
        {
             await SendBtn_ClickAsync(sender, e);
        }

        private async Task SendBtn_ClickAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_scriptAssembly == null)
                {
                     // Compile First
                     await CompileScriptsAsync(_workspacePath, AppendLog);
                     if (_scriptAssembly == null) return; 
                }

                if (_client == null || !_client.IsConnected)
                {
                     FluentMessageBox.ShowError("서버에 연결되지 않았습니다.");
                     return;
                }

                var type = (PacketListBox.SelectedItem as PacketConvertor)?.Type;
                if (type == null)
                {
                    AppendLog("No packet type selected.", Brushes.Red);
                    return;
                }

                var json = await GetEditorContentAsync(JsonEditorView);

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
                
                if (_headerAssembly != null)
                {
                     headerType = _headerAssembly.GetTypes().FirstOrDefault(t => typeof(IHeader).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                     if (headerType == null) headerType = _headerAssembly.GetTypes().FirstOrDefault(t => t.Name == "Header");
                }

                if (headerType == null)
                {
                    AppendLog("PacketHeader.csx has not been compiled or Header class not found.", Brushes.Red);
                    return;
                }

                // Header JSON to Object
                IHeader? headerObj = null;
                try 
                {
                    // Use HeaderJsonEditor.Text
                    headerObj = (IHeader?)JsonConvert.DeserializeObject(await GetEditorContentAsync(HeaderJsonEditorView), headerType);
                }
                catch(Exception ex)
                {
                    AppendLog($"Header JSON Error: {ex.Message}", Brushes.Red);
                    return;
                }

                if (headerObj == null)
                {
                    AppendLog("Header object is null.", Brushes.Red);
                    return;
                }

                var packet = new Packet(headerObj, message);

                // Encode & Send
                var bytes = ScriptGlobals.Codec.Encode(packet);
                _client.SendAsync(bytes.Span);

                AppendLog($"[Send] {message.GetType().Name} ({bytes.Length} bytes)", Brushes.White);
            }
            catch (Exception ex)
            {
                FluentMessageBox.ShowError($"전송 오류: {ex.Message}");
                AppendLog($"[Error] {ex.Message}", Brushes.Red);
            }
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
            Dispatcher.Invoke(() =>
            {
                _receiveBuffer.AddRange(data);
                ProcessReceiveBuffer();
            });
        }

        private void ProcessReceiveBuffer()
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
                    if(readSize > 0)
                    {
                        var consumed = span.Length - readSize;
                        _receiveBuffer.RemoveRange(0, (int)consumed);

                        if (packet != null)
                        {
                            var json = JsonConvert.SerializeObject(packet.Message, Formatting.Indented);
                            AppendLog($"[Recv] {packet.Message.GetType().Name} ({consumed} bytes)", Brushes.LimeGreen);
                            // Update Response Inspector
                             _ = SetEditorContentAsync(ResponseBoxView, json);
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
    }
}           