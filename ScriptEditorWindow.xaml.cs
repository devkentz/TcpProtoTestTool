using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private readonly Dictionary<string, Microsoft.Web.WebView2.Wpf.WebView2> _editors = new();
        private readonly HashSet<string> _dirtyFiles = new();
        private readonly Dictionary<string, List<DiagnosticDto>> _allDiagnostics = new();
        private RoslynIntelliSenseService? _intelliSense;
        private FileSystemWatcher? _fileWatcher;
        private readonly ScriptDebugger _debugger = new();
        private readonly Dictionary<string, HashSet<int>> _fileBreakpoints = new();

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
            await InitializeIntelliSenseAsync();
            await InitializeEditorsAsync();
            InitializeFileExplorer();
        }

        private void InitializeDebugger()
        {
            _debugger.BreakpointHit += (line, vars) =>
            {
                Dispatcher.Invoke(async () =>
                {
                    var activeTab = TabsPanel.Children.OfType<RadioButton>()
                        .FirstOrDefault(t => t.IsChecked == true);
                    if (activeTab?.Tag != null && _editors.TryGetValue(activeTab.Tag.ToString()!, out var editor))
                        await editor.ExecuteScriptAsync($"highlightDebugLine({line})");

                    VariablesListView.ItemsSource = vars;
                    DebugConsoleTab.IsChecked = true;
                    ContinueBtn.IsEnabled = true;
                    StepOverBtn.IsEnabled = true;
                    StopDebugBtn.IsEnabled = true;
                    AppendLog($"Breakpoint hit at line {line}", Brushes.Yellow);
                });
            };

            _debugger.OutputReceived += (output) =>
            {
                Dispatcher.Invoke(() => AppendLog(output.TrimEnd(), Brushes.LightGray));
            };

            _debugger.ErrorOccurred += (error) =>
            {
                Dispatcher.Invoke(() => AppendLog($"[Error] {error}", Brushes.Red));
            };

            _debugger.ExecutionCompleted += () =>
            {
                Dispatcher.Invoke(async () =>
                {
                    var activeTab = TabsPanel.Children.OfType<RadioButton>()
                        .FirstOrDefault(t => t.IsChecked == true);
                    if (activeTab?.Tag != null && _editors.TryGetValue(activeTab.Tag.ToString()!, out var editor))
                        await editor.ExecuteScriptAsync("clearDebugHighlight()");

                    ContinueBtn.IsEnabled = false;
                    StepOverBtn.IsEnabled = false;
                    StopDebugBtn.IsEnabled = false;
                    DebugBtn.IsEnabled = true;
                    SetStatus("Ready");
                    AppendLog("Debug session ended.", Brushes.Gray);
                });
            };
        }

        // ========== IntelliSense ==========

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

        public async Task RefreshIntelliSenseAsync()
        {
            await InitializeIntelliSenseAsync();
        }

        // ========== Editor Initialization ==========

        private async Task InitializeEditorsAsync()
        {
            // Clear dynamic tabs and editors
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
                MessageBox.Show($"Editor host not found at: {editorPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var editorUrl = new Uri(editorPath).AbsoluteUri;

            if (!Directory.Exists(_workspacePath)) return;

            var files = Directory.GetFiles(_workspacePath, "*.cs");
            bool isFirst = true;

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                await AddEditorTab(fileName, editorUrl, isFirst);
                isFirst = false;
            }

            SetStatus("Ready");
        }

        private async Task AddEditorTab(string fileName, string editorUrl, bool isSelected = false)
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
                Style = (Style)FindResource("EditorTabStyle"),
                IsChecked = isSelected
            };
            radio.Checked += Tab_Checked;
            TabsPanel.Children.Add(radio);

            await InitializeWebView(webView, editorUrl, fileName);
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
                            tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Editor Msg Error] {ex.Message}");
                    }
                };

                webView.Source = new Uri(url);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                if (completedTask == tcs.Task)
                {
                    await LoadFileIntoEditor(webView, fileName);
                }
                else
                {
                    AppendLog($"[Editor] Timeout for: {fileName}", Brushes.Orange);
                    await LoadFileIntoEditor(webView, fileName);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to init editor for {fileName}: {ex.Message}", Brushes.Red);
            }
        }

        private async Task LoadFileIntoEditor(Microsoft.Web.WebView2.Wpf.WebView2 webView, string fileName)
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

        // ========== Message Handler ==========

        private async Task HandleWebMessageAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string json, string fileName)
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
                    HandleCommand(root);
                    break;
                case "cursorPosition":
                    HandleCursorPosition(root);
                    break;
                case "contentChanged":
                    HandleContentChanged(root);
                    break;
                case "navigateToFile":
                    await HandleNavigateToFile(root);
                    break;
                case "breakpoint":
                    HandleBreakpointToggle(root);
                    break;
            }
        }

        // ========== Roslyn Request Handlers ==========

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
            _allDiagnostics[fileName] = diagnostics;
            var jsonResult = JsonSerializer.Serialize(diagnostics);
            await webView.ExecuteScriptAsync($"setDiagnostics({jsonResult})");
            Dispatcher.Invoke(UpdateProblemsPanel);
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

            // Apply cross-file edits
            if (result != null)
            {
                foreach (var fileEdit in result.Edits.Where(e => e.FileName != fileName))
                {
                    if (_editors.TryGetValue(fileEdit.FileName, out var otherEditor))
                    {
                        var editsJson = JsonSerializer.Serialize(fileEdit.TextEdits);
                        await otherEditor.ExecuteScriptAsync($"applyEdits({editsJson})");
                        MarkDirty(fileEdit.FileName);
                    }
                }
            }
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

        // ========== Command / UI Handlers ==========

        private void HandleCommand(JsonElement root)
        {
            var command = root.GetProperty("command").GetString();
            Dispatcher.Invoke(() =>
            {
                switch (command)
                {
                    case "save":
                        _ = SaveCurrentFileAsync();
                        break;
                    case "saveAll":
                        _ = SaveAllFilesAsync();
                        break;
                    case "build":
                        BuildBtn_Click(this, new RoutedEventArgs());
                        break;
                    case "searchFiles":
                        SearchToggle.IsChecked = true;
                        SearchTextBox.Focus();
                        break;
                    case "closeTab":
                        CloseCurrentTab();
                        break;
                }
            });
        }

        private void HandleCursorPosition(JsonElement root)
        {
            var line = root.GetProperty("line").GetInt32();
            var column = root.GetProperty("column").GetInt32();
            Dispatcher.Invoke(() => CursorPositionText.Text = $"Ln {line}, Col {column}");
        }

        private void HandleContentChanged(JsonElement root)
        {
            if (root.TryGetProperty("fileName", out var fn))
            {
                var fileName = fn.GetString();
                if (!string.IsNullOrEmpty(fileName))
                    Dispatcher.Invoke(() => MarkDirty(fileName));
            }
        }

        private async Task HandleNavigateToFile(JsonElement root)
        {
            var fileName = root.GetProperty("fileName").GetString() ?? "";
            var line = root.GetProperty("line").GetInt32();
            var column = root.GetProperty("column").GetInt32();

            await Dispatcher.InvokeAsync(async () =>
            {
                await OpenFileAndNavigate(fileName, line, column);
            });
        }

        private void HandleBreakpointToggle(JsonElement root)
        {
            var line = root.GetProperty("line").GetInt32();
            var enabled = root.GetProperty("enabled").GetBoolean();

            // Track per-file breakpoints
            var activeTab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.IsChecked == true);
            if (activeTab?.Tag == null) return;
            var fileName = activeTab.Tag.ToString()!;

            if (!_fileBreakpoints.TryGetValue(fileName, out var bps))
            {
                bps = [];
                _fileBreakpoints[fileName] = bps;
            }

            if (enabled)
            {
                bps.Add(line);
                _debugger.AddBreakpoint(line);
            }
            else
            {
                bps.Remove(line);
                _debugger.RemoveBreakpoint(line);
            }
        }

        // ========== Tab Management ==========

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton button || button.Tag == null) return;
            string fileName = button.Tag.ToString()!;

            foreach (var kvp in _editors)
            {
                kvp.Value.Visibility = kvp.Key.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Hidden;
            }
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;
            var fileName = btn.Tag.ToString()!;
            CloseTab(fileName);
        }

        private void CloseTab(string fileName)
        {
            if (_dirtyFiles.Contains(fileName))
            {
                var result = MessageBox.Show(
                    $"'{fileName}' has unsaved changes. Save before closing?",
                    "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes) _ = SaveFileAsync(fileName);
            }

            if (_editors.TryGetValue(fileName, out var editor))
            {
                editor.Dispose();
                EditorsGrid.Children.Remove(editor);
                _editors.Remove(fileName);
            }

            var tab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.Tag?.ToString() == fileName);
            if (tab != null) TabsPanel.Children.Remove(tab);

            _dirtyFiles.Remove(fileName);

            // Select next tab
            var firstTab = TabsPanel.Children.OfType<RadioButton>().FirstOrDefault();
            if (firstTab != null) firstTab.IsChecked = true;
        }

        private void CloseCurrentTab()
        {
            var activeTab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.IsChecked == true);
            if (activeTab?.Tag != null)
                CloseTab(activeTab.Tag.ToString()!);
        }

        private void MarkDirty(string fileName)
        {
            if (_dirtyFiles.Add(fileName))
            {
                var tab = TabsPanel.Children.OfType<RadioButton>()
                    .FirstOrDefault(t => t.Tag?.ToString() == fileName);
                if (tab != null)
                    tab.Content = Path.GetFileNameWithoutExtension(fileName) + " \u25cf";
            }
        }

        private void MarkClean(string fileName)
        {
            _dirtyFiles.Remove(fileName);
            var tab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.Tag?.ToString() == fileName);
            if (tab != null)
                tab.Content = Path.GetFileNameWithoutExtension(fileName);
        }

        private async Task OpenFileAndNavigate(string fileName, int line, int column)
        {
            if (!_editors.ContainsKey(fileName))
            {
                var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
                var editorUrl = new Uri(editorPath).AbsoluteUri;
                await AddEditorTab(fileName, editorUrl);
            }

            // Select tab
            var tab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.Tag?.ToString() == fileName);
            if (tab != null) tab.IsChecked = true;

            // Navigate to position
            if (_editors.TryGetValue(fileName, out var editor))
            {
                await Task.Delay(100); // Wait for tab switch
                await editor.ExecuteScriptAsync($"revealPosition({line}, {column})");
            }
        }

        // ========== File Operations ==========

        private async Task<string> GetEditorContent(Microsoft.Web.WebView2.Wpf.WebView2 webView)
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

        private async Task SaveCurrentFileAsync()
        {
            var activeTab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.IsChecked == true);
            if (activeTab?.Tag != null)
                await SaveFileAsync(activeTab.Tag.ToString()!);
        }

        private async Task SaveFileAsync(string fileName)
        {
            if (!_editors.TryGetValue(fileName, out var editor)) return;

            var code = await GetEditorContent(editor);
            if (string.IsNullOrWhiteSpace(code)) return;

            var path = Path.Combine(_workspacePath, fileName);
            await File.WriteAllTextAsync(path, code);
            MarkClean(fileName);
        }

        private async Task SaveAllFilesAsync()
        {
            foreach (var kvp in _editors)
            {
                var code = await GetEditorContent(kvp.Value);
                if (string.IsNullOrWhiteSpace(code)) continue;

                var path = Path.Combine(_workspacePath, kvp.Key);
                await File.WriteAllTextAsync(path, code);
                MarkClean(kvp.Key);
            }

            AppendLog($"Saved {_editors.Count} files.", Brushes.Gray);
        }

        // ========== File Explorer ==========

        private void InitializeFileExplorer()
        {
            RefreshFileTree();

            if (Directory.Exists(_workspacePath))
            {
                _fileWatcher = new FileSystemWatcher(_workspacePath, "*.cs")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _fileWatcher.Created += (s, e) => Dispatcher.Invoke(RefreshFileTree);
                _fileWatcher.Deleted += (s, e) => Dispatcher.Invoke(RefreshFileTree);
                _fileWatcher.Renamed += (s, e) => Dispatcher.Invoke(RefreshFileTree);
            }
        }

        private void RefreshFileTree()
        {
            FileTreeView.Items.Clear();

            if (!Directory.Exists(_workspacePath)) return;

            var scriptsItem = new TreeViewItem
            {
                Header = "Scripts",
                IsExpanded = true,
                Foreground = Brushes.LightGray
            };

            foreach (var file in Directory.GetFiles(_workspacePath, "*.cs").OrderBy(f => f))
            {
                var item = new TreeViewItem
                {
                    Header = Path.GetFileName(file),
                    Tag = file,
                    Foreground = Brushes.LightGray
                };
                scriptsItem.Items.Add(item);
            }

            FileTreeView.Items.Add(scriptsItem);

            // Libs folder (read-only indicator)
            var libsDir = Path.Combine(_workspacePath, "Libs");
            if (Directory.Exists(libsDir))
            {
                var libsItem = new TreeViewItem
                {
                    Header = "Libs",
                    IsExpanded = false,
                    Foreground = Brushes.Gray
                };

                foreach (var file in Directory.GetFiles(libsDir, "*.dll").OrderBy(f => f))
                {
                    libsItem.Items.Add(new TreeViewItem
                    {
                        Header = Path.GetFileName(file),
                        Foreground = Brushes.Gray
                    });
                }

                FileTreeView.Items.Add(libsItem);
            }
        }

        private async void FileTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileTreeView.SelectedItem is not TreeViewItem item || item.Tag == null) return;
            var filePath = item.Tag.ToString()!;
            var fileName = Path.GetFileName(filePath);

            await OpenFileAndNavigate(fileName, 1, 1);
        }

        private async void NewFile_Click(object sender, RoutedEventArgs e)
        {
            var fileName = "NewScript.cs";
            int count = 1;
            while (File.Exists(Path.Combine(_workspacePath, fileName)))
                fileName = $"NewScript{count++}.cs";

            var template = "using System;\n\nnamespace Scripts\n{\n    public class NewScript\n    {\n    }\n}\n";
            await File.WriteAllTextAsync(Path.Combine(_workspacePath, fileName), template);

            var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
            await AddEditorTab(fileName, new Uri(editorPath).AbsoluteUri, false);

            var tab = TabsPanel.Children.OfType<RadioButton>().LastOrDefault();
            if (tab != null) tab.IsChecked = true;

            RefreshFileTree();
            AppendLog($"Created {fileName}", Brushes.DeepSkyBlue);
        }

        // ========== Search ==========

        private void SearchBtn_Click(object sender, RoutedEventArgs e) => PerformSearch();

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) PerformSearch();
        }

        private void PerformSearch()
        {
            var query = SearchTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(query) || !Directory.Exists(_workspacePath)) return;

            SearchResultsList.Items.Clear();

            foreach (var file in Directory.GetFiles(_workspacePath, "*.cs"))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        SearchResultsList.Items.Add(new SearchResultItem
                        {
                            FileName = Path.GetFileName(file),
                            Line = i + 1,
                            Preview = lines[i].Trim(),
                            FullPath = file
                        });
                    }
                }
            }
        }

        private async void ReplaceAllBtn_Click(object sender, RoutedEventArgs e)
        {
            var search = SearchTextBox.Text?.Trim();
            var replace = ReplaceTextBox.Text ?? "";
            if (string.IsNullOrEmpty(search)) return;

            int totalReplacements = 0;
            foreach (var kvp in _editors)
            {
                var content = await GetEditorContent(kvp.Value);
                if (content.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    var newContent = Regex.Replace(content, Regex.Escape(search), replace, RegexOptions.IgnoreCase);
                    var safeContent = JsonSerializer.Serialize(newContent);
                    await kvp.Value.ExecuteScriptAsync($"setContent({safeContent})");
                    MarkDirty(kvp.Key);
                    totalReplacements++;
                }
            }

            AppendLog($"Replaced in {totalReplacements} file(s).", Brushes.DeepSkyBlue);
        }

        private async void SearchResult_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SearchResultsList.SelectedItem is not SearchResultItem item) return;
            await OpenFileAndNavigate(item.FileName, item.Line, 1);
        }

        // ========== Sidebar Toggle ==========

        private void SidebarToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle) return;

            // Uncheck other toggles
            if (toggle == ExplorerToggle && toggle.IsChecked == true)
                SearchToggle.IsChecked = false;
            else if (toggle == SearchToggle && toggle.IsChecked == true)
                ExplorerToggle.IsChecked = false;

            bool anyChecked = ExplorerToggle.IsChecked == true || SearchToggle.IsChecked == true;
            SidebarPanel.Visibility = anyChecked ? Visibility.Visible : Visibility.Collapsed;
            ExplorerPanel.Visibility = ExplorerToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SearchPanel.Visibility = SearchToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ========== Bottom Panel ==========

        private void PanelTab_Checked(object sender, RoutedEventArgs e)
        {
            if (ScriptLogBox == null) return; // Not yet loaded

            ScriptLogBox.Visibility = OutputTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ProblemsListView.Visibility = ProblemsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DebugConsolePanel.Visibility = DebugConsoleTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateProblemsPanel()
        {
            ProblemsListView.Items.Clear();
            int errors = 0, warnings = 0;

            foreach (var kvp in _allDiagnostics)
            {
                foreach (var diag in kvp.Value)
                {
                    ProblemsListView.Items.Add(new ProblemItem
                    {
                        Severity = diag.Severity == 8 ? "Error" : "Warning",
                        Message = diag.Message,
                        FileName = kvp.Key,
                        Line = diag.StartLine
                    });

                    if (diag.Severity == 8) errors++;
                    else warnings++;
                }
            }

            ErrorCountText.Text = errors.ToString();
            WarningCountText.Text = warnings.ToString();
            ProblemCountText.Text = errors + warnings > 0 ? $"({errors + warnings})" : "";
        }

        private async void ProblemItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProblemsListView.SelectedItem is not ProblemItem item) return;
            await OpenFileAndNavigate(item.FileName, item.Line, 1);
        }

        // ========== Toolbar Actions ==========

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            await SaveAllFilesAsync();
            SetStatus("Saved");
        }

        private async void ReloadBtn_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Discard changes and reload from disk?", "Reload",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await InitializeEditorsAsync();
                RefreshFileTree();
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

        private async void DebugBtn_Click(object sender, RoutedEventArgs e)
        {
            var activeTab = TabsPanel.Children.OfType<RadioButton>()
                .FirstOrDefault(t => t.IsChecked == true);
            if (activeTab?.Tag == null) return;

            var fileName = activeTab.Tag.ToString()!;
            if (!_editors.TryGetValue(fileName, out var editor)) return;

            var code = await GetEditorContent(editor);
            if (string.IsNullOrWhiteSpace(code))
            {
                AppendLog("[Debug] No code to debug.", Brushes.Orange);
                return;
            }

            DebugBtn.IsEnabled = false;
            StopDebugBtn.IsEnabled = true;
            DebugConsoleTab.IsChecked = true;
            SetStatus("Debugging...");
            AppendLog($"Starting debug: {fileName}", Brushes.DeepSkyBlue);

            await _debugger.ExecuteWithDebuggerAsync(code);
        }

        private void PackagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_workspacePath)) return;
            var window = new NuGetWindow(_workspacePath) { Owner = this };
            window.ShowDialog();
        }

        // ========== Interceptor Creation ==========

        private void AddProxyInterceptor_Click(object sender, RoutedEventArgs e) => CreateInterceptor("ProxyInterceptor", "PacketInterceptor_Proxy");
        private void AddClientInterceptor_Click(object sender, RoutedEventArgs e) => CreateInterceptor("ClientInterceptor", "PacketInterceptor_Client");
        private void AddReplayInterceptor_Click(object sender, RoutedEventArgs e) => CreateInterceptor("ReplayInterceptor", "PacketInterceptor_Replay");

        private async void CreateInterceptor(string baseName, string templateType)
        {
            string fileName = $"{baseName}.cs";
            int count = 1;
            while (File.Exists(Path.Combine(_workspacePath, fileName)))
                fileName = $"{baseName}{count++}.cs";

            try
            {
                var template = ScriptTemplateFactory.GetTemplate(templateType);
                await File.WriteAllTextAsync(Path.Combine(_workspacePath, fileName), template);

                var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
                await AddEditorTab(fileName, new Uri(editorPath).AbsoluteUri);

                var newTab = TabsPanel.Children.OfType<RadioButton>().LastOrDefault();
                if (newTab != null) newTab.IsChecked = true;

                RefreshFileTree();
                AppendLog($"Created {fileName}", Brushes.DeepSkyBlue);
            }
            catch (Exception ex)
            {
                AppendLog($"Create File Error: {ex.Message}", Brushes.Red);
            }
        }

        // ========== Debug Controls (Phase 5 placeholders) ==========

        private void ContinueBtn_Click(object sender, RoutedEventArgs e)
        {
            _debugger.Continue();
            ContinueBtn.IsEnabled = false;
            StepOverBtn.IsEnabled = false;
        }

        private void StepOverBtn_Click(object sender, RoutedEventArgs e)
        {
            // Step Over = Continue + will break on next line
            _debugger.Continue();
            ContinueBtn.IsEnabled = false;
            StepOverBtn.IsEnabled = false;
        }

        private void StopDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            _debugger.Stop();
        }

        // ========== Legacy Compatibility ==========

        public Task UpdateCompletionsAsync(string json) => Task.CompletedTask;

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

    // ========== Data Models ==========

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
