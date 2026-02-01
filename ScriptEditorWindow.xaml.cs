using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private readonly ScriptDebugger _debugger = new();
        private readonly VsCodeServerManager _vsCodeManager = new();

        private EditorMode _editorMode = EditorMode.Loading;

        // Fallback: Monaco editor state
        private readonly Dictionary<string, Microsoft.Web.WebView2.Wpf.WebView2> _editors = new();
        private readonly HashSet<string> _dirtyFiles = new();
        private RoslynIntelliSenseService? _intelliSense;

        public event Action? OnRequestCompilation;

        public ScriptEditorWindow(string workspacePath, ScriptLoader scriptLoader)
        {
            InitializeComponent();
            _workspaceRoot = workspacePath;
            _workspacePath = Path.Combine(workspacePath, "Scripts");
            _scriptLoader = scriptLoader;

            Loaded += ScriptEditorWindow_Loaded;
            InitializeDebugger();
        }

        private async void ScriptEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_vsCodeManager.IsVsCodeInstalled)
            {
                await StartVsCodeModeAsync();
            }
            else
            {
                await StartMonacoFallbackAsync();
            }
        }

        // ========== VS Code Server Mode ==========

        private async Task StartVsCodeModeAsync()
        {
            _editorMode = EditorMode.Loading;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Starting VS Code Server...";

            try
            {
                // Ensure workspace setup (.csproj, .vscode settings)
                VsCodeWorkspaceSetup.EnsureWorkspaceSetup(_workspaceRoot);
                LoadingSubText.Text = "Workspace configured";

                _vsCodeManager.OutputReceived += msg =>
                    Dispatcher.Invoke(() => AppendLog($"[Server] {msg}", Brushes.Gray));

                _vsCodeManager.ServerStopped += () =>
                    Dispatcher.Invoke(() =>
                    {
                        ServerStatusDot.Fill = Brushes.Red;
                        ServerStatusText.Text = "Server Stopped";
                        RestartServerBtn.Visibility = Visibility.Visible;
                    });

                LoadingText.Text = "Launching VS Code Server...";
                LoadingSubText.Text = "This may take a moment on first launch";

                var port = await _vsCodeManager.StartServerAsync(_workspacePath);

                _editorMode = EditorMode.VsCode;
                LoadingText.Text = "Loading VS Code...";
                LoadingSubText.Text = $"Port {port}";

                // Navigate WebView2 to VS Code
                var env = await CoreWebView2Environment.CreateAsync();
                await VsCodeWebView.EnsureCoreWebView2Async(env);
                VsCodeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                VsCodeWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                VsCodeWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                var folderPath = _workspacePath.Replace("\\", "/");
                var url = $"http://127.0.0.1:{port}/?folder={folderPath}";
                VsCodeWebView.Source = new Uri(url);

                // Wait for VS Code to finish loading
                VsCodeWebView.NavigationCompleted += (s, args) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        VsCodeWebView.Visibility = Visibility.Visible;

                        ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                        ServerStatusText.Text = "VS Code Server";
                        EditorModeText.Text = "VS Code";
                        PortText.Text = $"Port {port}";
                        RestartServerBtn.Visibility = Visibility.Visible;
                    });
                };

                AppendLog($"VS Code Server started on port {port}", Brushes.LimeGreen);
            }
            catch (Exception ex)
            {
                AppendLog($"VS Code Server failed: {ex.Message}", Brushes.Red);
                AppendLog("Falling back to built-in Monaco editor...", Brushes.Orange);
                await StartMonacoFallbackAsync();
            }
        }

        // ========== Monaco Fallback Mode ==========

        private async Task StartMonacoFallbackAsync()
        {
            _editorMode = EditorMode.Monaco;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            VsCodeWebView.Visibility = Visibility.Collapsed;
            MonacoFallbackGrid.Visibility = Visibility.Visible;

            ServerStatusDot.Fill = Brushes.Orange;
            ServerStatusText.Text = "Built-in Editor";
            EditorModeText.Text = "Monaco (Fallback)";
            PortText.Text = "";

            if (!_vsCodeManager.IsVsCodeInstalled)
            {
                AppendLog("VS Code not found. Using built-in Monaco editor.", Brushes.Orange);
                AppendLog("Install VS Code for full IDE experience: https://code.visualstudio.com", Brushes.Gray);
            }

            await InitializeIntelliSenseAsync();
            await InitializeMonacoEditorsAsync();
        }

        private async Task InitializeIntelliSenseAsync()
        {
            try
            {
                _intelliSense = new RoslynIntelliSenseService();

                var additionalRefs = new List<string>();
                var libsDir = Path.Combine(_workspacePath, "Libs");
                if (Directory.Exists(libsDir))
                    additionalRefs.AddRange(Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories));

                await _intelliSense.InitializeAsync(_workspaceRoot, additionalRefs);
                AppendLog("IntelliSense initialized.", Brushes.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"IntelliSense init failed: {ex.Message}", Brushes.Orange);
            }
        }

        private async Task InitializeMonacoEditorsAsync()
        {
            TabsPanel.Children.Clear();
            foreach (var editor in _editors.Values)
                editor.Dispose();
            foreach (var child in EditorsGrid.Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().ToList())
                EditorsGrid.Children.Remove(child);
            _editors.Clear();
            _dirtyFiles.Clear();

            var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
            if (!File.Exists(editorPath))
            {
                AppendLog($"Editor host not found: {editorPath}", Brushes.Red);
                return;
            }
            var editorUrl = new Uri(editorPath).AbsoluteUri;

            if (!Directory.Exists(_workspacePath)) return;

            var files = Directory.GetFiles(_workspacePath, "*.cs");
            bool isFirst = true;

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                await AddMonacoTab(fileName, editorUrl, isFirst);
                isFirst = false;
            }
        }

        private async Task AddMonacoTab(string fileName, string editorUrl, bool isSelected = false)
        {
            var webView = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                Visibility = isSelected ? Visibility.Visible : Visibility.Hidden
            };
            EditorsGrid.Children.Add(webView);
            _editors[fileName] = webView;

            var radio = new RadioButton
            {
                Content = Path.GetFileNameWithoutExtension(fileName),
                Tag = fileName,
                IsChecked = isSelected
            };
            radio.Checked += MonacoTab_Checked;
            TabsPanel.Children.Add(radio);

            try
            {
                var tcs = new TaskCompletionSource<bool>();

                webView.WebMessageReceived += async (s, e) =>
                {
                    try
                    {
                        await HandleMonacoMessageAsync(webView, e.WebMessageAsJson, fileName);
                        if (e.WebMessageAsJson.Contains("\"type\":\"ready\""))
                            tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Editor Msg Error] {ex.Message}");
                    }
                };

                webView.Source = new Uri(editorUrl);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                await LoadFileIntoMonaco(webView, fileName);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to init editor for {fileName}: {ex.Message}", Brushes.Red);
            }
        }

        private async Task LoadFileIntoMonaco(Microsoft.Web.WebView2.Wpf.WebView2 webView, string fileName)
        {
            var safeFileName = JsonSerializer.Serialize(fileName);
            await webView.ExecuteScriptAsync($"setFileName({safeFileName})");

            var path = Path.Combine(_workspacePath, fileName);
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                var safeContent = JsonSerializer.Serialize(content);
                await webView.ExecuteScriptAsync($"setContent({safeContent})");
                _intelliSense?.UpdateDocument(fileName, content);
            }
        }

        private void MonacoTab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton button || button.Tag == null) return;
            string fileName = button.Tag.ToString()!;

            foreach (var kvp in _editors)
            {
                kvp.Value.Visibility = kvp.Key.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // ========== Monaco Message Handler (Fallback) ==========

        private async Task HandleMonacoMessageAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string json, string fileName)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

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
                case "requestHover":
                    await HandleHoverRequestAsync(webView, root, fileName);
                    break;
                case "requestDefinition":
                    await HandleDefinitionRequestAsync(webView, root, fileName);
                    break;
                case "requestReferences":
                    await HandleReferencesRequestAsync(webView, root, fileName);
                    break;
                case "requestCodeActions":
                    await HandleCodeActionsRequestAsync(webView, root, fileName);
                    break;
                case "applyCodeAction":
                    await HandleApplyCodeActionAsync(webView, root, fileName);
                    break;
                case "requestFormatting":
                    await HandleFormattingRequestAsync(webView, root, fileName);
                    break;
                case "requestRename":
                    await HandleRenameRequestAsync(webView, root, fileName);
                    break;
                case "requestSymbols":
                    await HandleSymbolsRequestAsync(webView, root, fileName);
                    break;
                case "command":
                    HandleMonacoCommand(root);
                    break;
                case "contentChanged":
                    HandleMonacoContentChanged(root);
                    break;
                case "breakpoint":
                    HandleMonacoBreakpoint(root);
                    break;
            }
        }

        private async Task HandleCompletionRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var completions = await _intelliSense.GetCompletionsAsync(fileName, position);
            var jsonResult = JsonSerializer.Serialize(completions);
            await webView.ExecuteScriptAsync($"setCompletions({jsonResult})");
        }

        private async Task HandleSignatureHelpRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var signatureHelp = await _intelliSense.GetSignatureHelpAsync(fileName, position);
            var jsonResult = signatureHelp != null ? JsonSerializer.Serialize(signatureHelp) : "null";
            await webView.ExecuteScriptAsync($"setSignatureHelp({jsonResult})");
        }

        private async Task HandleDiagnosticsRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var diagnostics = await _intelliSense.GetDiagnosticsAsync(fileName);
            var jsonResult = JsonSerializer.Serialize(diagnostics);
            await webView.ExecuteScriptAsync($"setDiagnostics({jsonResult})");
        }

        private async Task HandleHoverRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var hover = await _intelliSense.GetHoverAsync(fileName, position);
            var jsonResult = hover != null ? JsonSerializer.Serialize(hover) : "null";
            await webView.ExecuteScriptAsync($"setHover({jsonResult})");
        }

        private async Task HandleDefinitionRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var definition = await _intelliSense.GetDefinitionAsync(fileName, position);
            var jsonResult = definition != null ? JsonSerializer.Serialize(definition) : "null";
            await webView.ExecuteScriptAsync($"setDefinition({jsonResult})");
        }

        private async Task HandleReferencesRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var references = await _intelliSense.GetReferencesAsync(fileName, position);
            var jsonResult = JsonSerializer.Serialize(references);
            await webView.ExecuteScriptAsync($"setReferences({jsonResult})");
        }

        private async Task HandleCodeActionsRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var startPos = root.GetProperty("startPosition").GetInt32();
            var endPos = root.GetProperty("endPosition").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var actions = await _intelliSense.GetCodeActionsAsync(fileName, startPos, endPos);
            var jsonResult = JsonSerializer.Serialize(actions);
            await webView.ExecuteScriptAsync($"setCodeActions({jsonResult})");
        }

        private async Task HandleApplyCodeActionAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var actionIndex = root.GetProperty("actionIndex").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var result = await _intelliSense.ApplyCodeActionAsync(fileName, actionIndex);
            if (result != null)
            {
                var safeContent = JsonSerializer.Serialize(result.NewContent);
                await webView.ExecuteScriptAsync($"setContent({safeContent})");
            }
        }

        private async Task HandleFormattingRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var edits = await _intelliSense.FormatDocumentAsync(fileName);
            var jsonResult = JsonSerializer.Serialize(edits);
            await webView.ExecuteScriptAsync($"setFormatting({jsonResult})");
        }

        private async Task HandleRenameRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var newName = root.GetProperty("newName").GetString() ?? "";
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var result = await _intelliSense.RenameSymbolAsync(fileName, position, newName);
            var jsonResult = result != null ? JsonSerializer.Serialize(result) : "{ \"edits\": [] }";
            await webView.ExecuteScriptAsync($"setRenameEdits({jsonResult})");
        }

        private async Task HandleSymbolsRequestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var symbols = await _intelliSense.GetDocumentSymbolsAsync(fileName);
            var jsonResult = JsonSerializer.Serialize(symbols);
            await webView.ExecuteScriptAsync($"setDocumentSymbols({jsonResult})");
        }

        private void HandleMonacoCommand(JsonElement root)
        {
            var command = root.GetProperty("command").GetString();
            Dispatcher.Invoke(() =>
            {
                switch (command)
                {
                    case "save":
                        _ = SaveCurrentMonacoFileAsync();
                        break;
                    case "build":
                        BuildBtn_Click(this, new RoutedEventArgs());
                        break;
                }
            });
        }

        private void HandleMonacoContentChanged(JsonElement root)
        {
            if (root.TryGetProperty("fileName", out var fn))
            {
                var fileName = fn.GetString();
                if (!string.IsNullOrEmpty(fileName))
                    Dispatcher.Invoke(() => MarkMonacoDirty(fileName));
            }
        }

        private void HandleMonacoBreakpoint(JsonElement root)
        {
            var line = root.GetProperty("line").GetInt32();
            var enabled = root.GetProperty("enabled").GetBoolean();

            if (enabled)
                _debugger.AddBreakpoint(line);
            else
                _debugger.RemoveBreakpoint(line);
        }

        private void MarkMonacoDirty(string fileName)
        {
            if (_dirtyFiles.Add(fileName))
            {
                var tab = TabsPanel.Children.OfType<RadioButton>()
                    .FirstOrDefault(t => t.Tag?.ToString() == fileName);
                if (tab != null)
                    tab.Content = Path.GetFileNameWithoutExtension(fileName) + " \u25cf";
            }
        }

        private void MarkMonacoClean(string fileName)
        {
            _dirtyFiles.Remove(fileName);
            var tab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.Tag?.ToString() == fileName);
            if (tab != null)
                tab.Content = Path.GetFileNameWithoutExtension(fileName);
        }

        // ========== Monaco File Operations ==========

        private async Task<string> GetMonacoEditorContent(Microsoft.Web.WebView2.Wpf.WebView2 webView)
        {
            try
            {
                var result = await webView.ExecuteScriptAsync("getContent()");
                return JsonSerializer.Deserialize<string>(result) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task SaveCurrentMonacoFileAsync()
        {
            var activeTab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.IsChecked == true);
            if (activeTab?.Tag == null) return;
            var fileName = activeTab.Tag.ToString()!;

            if (!_editors.TryGetValue(fileName, out var editor)) return;
            var code = await GetMonacoEditorContent(editor);
            if (string.IsNullOrWhiteSpace(code)) return;

            var path = Path.Combine(_workspacePath, fileName);
            await File.WriteAllTextAsync(path, code);
            MarkMonacoClean(fileName);
        }

        private async Task SaveAllMonacoFilesAsync()
        {
            foreach (var kvp in _editors)
            {
                var code = await GetMonacoEditorContent(kvp.Value);
                if (string.IsNullOrWhiteSpace(code)) continue;

                var path = Path.Combine(_workspacePath, kvp.Key);
                await File.WriteAllTextAsync(path, code);
                MarkMonacoClean(kvp.Key);
            }
        }

        // ========== Debugger ==========

        private void InitializeDebugger()
        {
            _debugger.BreakpointHit += (line, vars) =>
            {
                Dispatcher.Invoke(() =>
                {
                    VariablesListView.ItemsSource = vars;
                    DebugConsoleTab.IsChecked = true;
                    ContinueBtn.IsEnabled = true;
                    StopDebugBtn.IsEnabled = true;
                    AppendLog($"Breakpoint hit at line {line}", Brushes.Yellow);
                });
            };

            _debugger.OutputReceived += output =>
                Dispatcher.Invoke(() => AppendLog(output.TrimEnd(), Brushes.LightGray));

            _debugger.ErrorOccurred += error =>
                Dispatcher.Invoke(() => AppendLog($"[Error] {error}", Brushes.Red));

            _debugger.ExecutionCompleted += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    ContinueBtn.IsEnabled = false;
                    StopDebugBtn.IsEnabled = false;
                    DebugBtn.IsEnabled = true;
                    SetStatus("Ready");
                    AppendLog("Debug session ended.", Brushes.Gray);
                });
            };
        }

        // ========== Toolbar Actions ==========

        private async void BuildBtn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Building...";
            ScriptLogBox.Document.Blocks.Clear();
            BuildBtn.IsEnabled = false;

            try
            {
                if (_editorMode == EditorMode.Monaco)
                {
                    await SaveAllMonacoFilesAsync();
                    AppendLog($"Saved {_editors.Count} files.", Brushes.Gray);
                }
                else
                {
                    // VS Code auto-save: wait for flush
                    await Task.Delay(300);
                }

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

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _debugger.Stop();
        }

        private async void DebugBtn_Click(object sender, RoutedEventArgs e)
        {
            string? code = null;

            if (_editorMode == EditorMode.Monaco)
            {
                var activeTab = TabsPanel.Children.OfType<RadioButton>()
                    .FirstOrDefault(t => t.IsChecked == true);
                if (activeTab?.Tag == null) return;

                var fileName = activeTab.Tag.ToString()!;
                if (!_editors.TryGetValue(fileName, out var editor)) return;

                code = await GetMonacoEditorContent(editor);
            }
            else
            {
                // VS Code mode: read the active file from disk
                var csFiles = Directory.GetFiles(_workspacePath, "*.cs");
                if (csFiles.Length == 0)
                {
                    AppendLog("[Debug] No .cs files found.", Brushes.Orange);
                    return;
                }

                // Use first file or show dialog
                var targetFile = csFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("PacketInterceptor.cs", StringComparison.OrdinalIgnoreCase))
                    ?? csFiles[0];
                code = await File.ReadAllTextAsync(targetFile);
                AppendLog($"[Debug] Debugging: {Path.GetFileName(targetFile)}", Brushes.DeepSkyBlue);
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                AppendLog("[Debug] No code to debug.", Brushes.Orange);
                return;
            }

            DebugBtn.IsEnabled = false;
            StopDebugBtn.IsEnabled = true;
            DebugConsoleTab.IsChecked = true;
            SetStatus("Debugging...");

            await _debugger.ExecuteWithDebuggerAsync(code);
        }

        private void PackagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_workspacePath)) return;
            var window = new NuGetWindow(_workspacePath) { Owner = this };
            window.ShowDialog();
        }

        private async void RestartServerBtn_Click(object sender, RoutedEventArgs e)
        {
            _vsCodeManager.StopServer();
            VsCodeWebView.Visibility = Visibility.Collapsed;

            foreach (var editor in _editors.Values)
                editor.Dispose();
            _editors.Clear();
            EditorsGrid.Children.Clear();
            TabsPanel.Children.Clear();
            MonacoFallbackGrid.Visibility = Visibility.Collapsed;

            if (_vsCodeManager.IsVsCodeInstalled)
                await StartVsCodeModeAsync();
            else
                await StartMonacoFallbackAsync();
        }

        // ========== Debug Controls ==========

        private void ContinueBtn_Click(object sender, RoutedEventArgs e)
        {
            _debugger.Continue();
            ContinueBtn.IsEnabled = false;
        }

        private void StopDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            _debugger.Stop();
        }

        // ========== Bottom Panel ==========

        private void PanelTab_Checked(object sender, RoutedEventArgs e)
        {
            if (ScriptLogBox == null) return;

            ScriptLogBox.Visibility = OutputTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DebugConsolePanel.Visibility = DebugConsoleTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ========== Lifecycle ==========

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _vsCodeManager.Dispose();

            foreach (var editor in _editors.Values)
            {
                try { editor.Dispose(); } catch { }
            }
            _editors.Clear();
        }

        // ========== Legacy Compatibility ==========

        public Task UpdateCompletionsAsync(string json) => Task.CompletedTask;

        public async Task RefreshIntelliSenseAsync()
        {
            if (_editorMode == EditorMode.Monaco)
                await InitializeIntelliSenseAsync();
        }

        // ========== Logging ==========

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

    // ========== Enums & Data Models ==========

    public enum EditorMode
    {
        Loading,
        VsCode,
        Monaco
    }

    public class SearchResultItem
    {
        public string FileName { get; set; } = "";
        public int Line { get; set; }
        public string Preview { get; set; } = "";
        public string FullPath { get; set; } = "";

        public override string ToString() => $"{FileName}:{Line}  {Preview}";
    }

    public class ProblemItem
    {
        public string Severity { get; set; } = "";
        public string Message { get; set; } = "";
        public string FileName { get; set; } = "";
        public int Line { get; set; }
    }
}
