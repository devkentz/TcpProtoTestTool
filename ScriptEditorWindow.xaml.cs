using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using ProtoTestTool.Services;

namespace ProtoTestTool
{
    public partial class ScriptEditorWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly string _workspacePath;
        private readonly string _workspaceRoot;
        private readonly ScriptLoader _scriptLoader;
        private readonly System.Collections.Generic.Dictionary<string, string> _fileMapping = new();
        private RoslynIntelliSenseService? _intelliSense;

        public ScriptEditorWindow(string workspacePath, ScriptLoader scriptLoader)
        {
            InitializeComponent();
            _workspaceRoot = workspacePath;
            _workspacePath = Path.Combine(workspacePath, "Scripts");
            _scriptLoader = scriptLoader;

            Loaded += ScriptEditorWindow_Loaded;
        }

        private async void ScriptEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeIntelliSenseAsync();
            await InitializeEditorsAsync();
        }

        private async Task InitializeIntelliSenseAsync()
        {
            try
            {
                _intelliSense = new RoslynIntelliSenseService();

                // Collect additional references from Libs folder
                var additionalRefs = new System.Collections.Generic.List<string>();
                var libsDir = Path.Combine(_workspacePath, "Libs");
                if (Directory.Exists(libsDir))
                {
                    additionalRefs.AddRange(Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories));
                }

                await _intelliSense.InitializeAsync(_workspaceRoot, additionalRefs);
                AppendLog("IntelliSense initialized.", Brushes.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"IntelliSense init failed: {ex.Message}", Brushes.Orange);
            }
        }

        private async Task InitializeEditorsAsync()
        {
            var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
            if (!File.Exists(editorPath))
            {
                MessageBox.Show($"Editor host not found at: {editorPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var editorUrl = new Uri(editorPath).AbsoluteUri;

            // Resolve file paths (.cs only)
            _fileMapping["Registry"] = "PacketRegistry.cs";
            _fileMapping["Header"] = "PacketHeader.cs";
            _fileMapping["Codec"] = "PacketCodec.cs";
            _fileMapping["Interceptor"] = "PacketInterceptor.cs";

            // Initialize WebView2 for each tab
            await InitializeWebView(RegistryEditor, editorUrl, _fileMapping["Registry"]);
            await InitializeWebView(HeaderEditor, editorUrl, _fileMapping["Header"]);
            await InitializeWebView(CodecEditor, editorUrl, _fileMapping["Codec"]);
            await InitializeWebView(InterceptorEditor, editorUrl, _fileMapping["Interceptor"]);

            SetStatus("Ready");
        }

        private async Task InitializeWebView(Microsoft.Web.WebView2.Wpf.WebView2 webView, string url, string fileName)
        {
            try
            {
                var tcs = new TaskCompletionSource<bool>();

                webView.WebMessageReceived += async (s, e) =>
                {
                    try
                    {
                        await HandleWebMessageAsync(webView, e.WebMessageAsJson, fileName);

                        if (e.WebMessageAsJson.Contains("\"type\":\"ready\""))
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[Editor Msg Error] {ex.Message}", Brushes.Red);
                    }
                };

                webView.Source = new Uri(url);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
                if (completedTask == tcs.Task)
                {
                    await LoadFileIntoEditor(webView, fileName);
                }
                else
                {
                    AppendLog($"[Editor] Timeout waiting for editor ready: {fileName}", Brushes.Orange);
                    await LoadFileIntoEditor(webView, fileName);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to initialize editor for {fileName}: {ex.Message}", Brushes.Red);
            }
        }

        private async Task HandleWebMessageAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string json, string fileName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp))
                    return;

                var type = typeProp.GetString();
                System.Diagnostics.Debug.WriteLine($"[WebMessage] Type: {type}, File: {fileName}");

                switch (type)
                {
                    case "requestCompletions":
                        await HandleCompletionRequestAsync(webView, root, fileName);
                        break;

                    case "requestSignatureHelp":
                        await HandleSignatureHelpRequestAsync(webView, root, fileName);
                        break;

                    case "requestDiagnostics":
                        await HandleDiagnosticsRequestAsync(webView, root, fileName);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebMessage Error] {ex.Message}");
            }
        }

        private async Task HandleCompletionRequestAsync(
            Microsoft.Web.WebView2.Wpf.WebView2 webView,
            JsonElement root,
            string fileName)
        {
            if (_intelliSense == null)
            {
                System.Diagnostics.Debug.WriteLine("[Completion] IntelliSense is null!");
                return;
            }

            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";

            System.Diagnostics.Debug.WriteLine($"[Completion] File: {fileName}, Position: {position}, ContentLen: {content.Length}");

            // Update document in Roslyn workspace
            _intelliSense.UpdateDocument(fileName, content);

            // Get completions
            var completions = await _intelliSense.GetCompletionsAsync(fileName, position);

            System.Diagnostics.Debug.WriteLine($"[Completion] Got {completions.Count} items");

            // Send back to Monaco
            var jsonResult = JsonSerializer.Serialize(completions);
            await webView.ExecuteScriptAsync($"setCompletions({jsonResult})");
        }

        private async Task HandleSignatureHelpRequestAsync(
            Microsoft.Web.WebView2.Wpf.WebView2 webView,
            JsonElement root,
            string fileName)
        {
            if (_intelliSense == null) return;

            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";

            _intelliSense.UpdateDocument(fileName, content);

            var signatureHelp = await _intelliSense.GetSignatureHelpAsync(fileName, position);

            var jsonResult = signatureHelp != null
                ? JsonSerializer.Serialize(signatureHelp)
                : "null";
            await webView.ExecuteScriptAsync($"setSignatureHelp({jsonResult})");
        }

        private async Task HandleDiagnosticsRequestAsync(
            Microsoft.Web.WebView2.Wpf.WebView2 webView,
            JsonElement root,
            string fileName)
        {
            if (_intelliSense == null) return;

            var content = root.GetProperty("content").GetString() ?? "";

            _intelliSense.UpdateDocument(fileName, content);

            var diagnostics = await _intelliSense.GetDiagnosticsAsync(fileName);

            var jsonResult = JsonSerializer.Serialize(diagnostics);
            await webView.ExecuteScriptAsync($"setDiagnostics({jsonResult})");
        }

        private async Task LoadFileIntoEditor(Microsoft.Web.WebView2.Wpf.WebView2 webView, string fileName)
        {
            var path = Path.Combine(_workspacePath, fileName);

            // Set file name for Monaco
            var safeFileName = JsonSerializer.Serialize(fileName);
            await webView.ExecuteScriptAsync($"setFileName({safeFileName})");

            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                var safeContent = JsonSerializer.Serialize(content);
                await webView.ExecuteScriptAsync($"setContent({safeContent})");

                // Initialize document in Roslyn
                _intelliSense?.UpdateDocument(fileName, content);
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (RegistryEditor == null) return;

            var button = sender as System.Windows.Controls.Primitives.ToggleButton;
            if (button == null || button.Tag == null) return;

            string targetName = button.Tag.ToString()!;

            SetVisibility(RegistryEditor, targetName == "RegistryEditor");
            SetVisibility(HeaderEditor, targetName == "HeaderEditor");
            SetVisibility(CodecEditor, targetName == "CodecEditor");
            SetVisibility(InterceptorEditor, targetName == "InterceptorEditor");
        }

        private void SetVisibility(Microsoft.Web.WebView2.Wpf.WebView2 webView, bool isVisible)
        {
            if (webView == null) return;
            webView.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task<string> GetEditorContent(Microsoft.Web.WebView2.Wpf.WebView2 webView)
        {
            try
            {
                var result = await webView.ExecuteScriptAsync("getContent()");
                return JsonSerializer.Deserialize<string>(result) ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppendLog($"Error getting content: {ex.Message}", Brushes.Red);
                return string.Empty;
            }
        }

        private async Task SaveAllFilesAsync()
        {
            var registryCode = await GetEditorContent(RegistryEditor);
            var headerCode = await GetEditorContent(HeaderEditor);
            var codecCode = await GetEditorContent(CodecEditor);
            var interceptorCode = await GetEditorContent(InterceptorEditor);

            if (string.IsNullOrWhiteSpace(registryCode) || string.IsNullOrWhiteSpace(headerCode) ||
                string.IsNullOrWhiteSpace(codecCode) || string.IsNullOrWhiteSpace(interceptorCode))
            {
                AppendLog("Error: One or more editors are empty. Save Aborted.", Brushes.Red);
                return;
            }

            await File.WriteAllTextAsync(Path.Combine(_workspacePath, _fileMapping["Registry"]), registryCode);
            await File.WriteAllTextAsync(Path.Combine(_workspacePath, _fileMapping["Header"]), headerCode);
            await File.WriteAllTextAsync(Path.Combine(_workspacePath, _fileMapping["Codec"]), codecCode);
            await File.WriteAllTextAsync(Path.Combine(_workspacePath, _fileMapping["Interceptor"]), interceptorCode);

            AppendLog("Files Saved.", Brushes.Gray);
        }

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SaveAllFilesAsync();
                SetStatus("Saved");
            }
            catch (Exception ex)
            {
                AppendLog($"Save Error: {ex.Message}", Brushes.Red);
            }
        }

        private async void ReloadBtn_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Discard changes and reload from disk?", "Reload", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await InitializeEditorsAsync();
                AppendLog("Reloaded from disk.", Brushes.DeepSkyBlue);
            }
        }

        private async void BuildBtn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Building...";
            ScriptLogBox.Document.Blocks.Clear();
            BuildBtn.IsEnabled = false;

            try
            {
                await SaveAllFilesAsync();
                AppendLog("Requesting Compilation...", Brushes.Gray);

                OnRequestCompilation?.Invoke();
            }
            catch (Exception ex)
            {
                AppendLog($"Build Error: {ex.Message}", Brushes.Red);
                StatusText.Text = "Error";
            }
            finally
            {
                BuildBtn.IsEnabled = true;
            }
        }

        private void PackagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_workspacePath)) return;

            var window = new NuGetWindow(_workspacePath);
            window.Owner = this;
            window.ShowDialog();
        }

        public Task UpdateCompletionsAsync(string json)
        {
            // Legacy - now handled by Roslyn IntelliSense
            // Keep for compatibility if needed
            return Task.CompletedTask;
        }

        public async Task RefreshIntelliSenseAsync()
        {
            // Reinitialize IntelliSense (e.g., after Proto recompilation)
            await InitializeIntelliSenseAsync();
        }

        public event Action? OnRequestCompilation;

        public void AppendLog(string message, Brush color)
        {
            Dispatcher.Invoke(() =>
            {
                var paragraph = new System.Windows.Documents.Paragraph();
                var run = new System.Windows.Documents.Run($"[{DateTime.Now:HH:mm:ss}] {message}") 
                { 
                    Foreground = color 
                };
                paragraph.Inlines.Add(run);
                ScriptLogBox.Document.Blocks.Add(paragraph);
                ScriptLogBox.ScrollToEnd();
            });
        }

        public void SetStatus(string status)
        {
            Dispatcher.Invoke(() => StatusText.Text = status);
        }
    }
}
