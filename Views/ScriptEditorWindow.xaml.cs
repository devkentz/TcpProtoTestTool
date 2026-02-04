using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using ProtoTestTool.Controls;
using ProtoTestTool.Services;

namespace ProtoTestTool.Views
{
    public partial class ScriptEditorWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly string _workspacePath;
        private readonly string _workspaceRoot;

        // Single WebView2 + file content cache
        private readonly Dictionary<string, string> _fileContents = new();
        private readonly HashSet<string> _dirtyFiles = new();
        private string? _activeFileName;
        private bool _editorReady;

        // Roslyn IntelliSense
        private RoslynIntelliSenseService? _intelliSense;

        // Core Script file names (fixed set)
        private static readonly string[] CoreScripts = ["PacketCodec.cs", "PacketHeader.cs", "PacketRegistry.cs"];

        public event Action? OnRequestCompilation;

        public ScriptEditorWindow(string workspacePath)
        {
            InitializeComponent();
            _workspaceRoot = workspacePath;
            _workspacePath = Path.Combine(workspacePath, "Scripts");

            Loaded += ScriptEditorWindow_Loaded;
        }

        private async void ScriptEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeEditorAsync();
        }

        // ========== Initialization ==========

        private async Task InitializeEditorAsync()
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Initializing Editor...";

            try
            {
                await InitializeIntelliSenseAsync();
                await InitializeWebViewAsync();
                LoadAllFiles();
                RefreshInterceptorList();
                RefreshLibsList();

                // Open first core script by default
                if (_fileContents.ContainsKey(CoreScripts[0]))
                    await SwitchToFileAsync(CoreScripts[0]);
                else if (_fileContents.Count > 0)
                    await SwitchToFileAsync(_fileContents.Keys.First());

                LoadingOverlay.Visibility = Visibility.Collapsed;
                EditorWebView.Visibility = Visibility.Visible;
                SetStatus("Ready");
            }
            catch (Exception ex)
            {
                AppendLog($"Initialization failed: {ex.Message}", Brushes.Red);
                FileLogger.Instance.Error("ScriptEditorWindow.InitializeEditorAsync failed", ex);
                LoadingText.Text = $"Error: {ex.Message}";
            }
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
                FileLogger.Instance.Error("InitializeIntelliSenseAsync failed", ex);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
            if (!File.Exists(editorPath))
            {
                AppendLog($"Editor host not found: {editorPath}", Brushes.Red);
                return;
            }

            var env = await CoreWebView2Environment.CreateAsync();
            await EditorWebView.EnsureCoreWebView2Async(env);
            EditorWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            EditorWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            EditorWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            var tcs = new TaskCompletionSource<bool>();

            EditorWebView.WebMessageReceived += async (s, e) =>
            {
                try
                {
                    await HandleMonacoMessageAsync(e.WebMessageAsJson);
                    if (e.WebMessageAsJson.Contains("\"type\":\"ready\""))
                        tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Editor Msg Error] {ex.Message}");
                }
            };

            EditorWebView.Source = new Uri(new Uri(editorPath).AbsoluteUri);

            // Wait for editor ready (max 10s)
            await Task.WhenAny(tcs.Task, Task.Delay(10000));
            _editorReady = true;
        }

        private void LoadAllFiles()
        {
            _fileContents.Clear();
            if (!Directory.Exists(_workspacePath)) return;

            var files = Directory.GetFiles(_workspacePath, "*.cs");
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var content = File.ReadAllText(file);
                _fileContents[fileName] = content;
                _intelliSense?.UpdateDocument(fileName, content);
            }
        }

        // ========== File Switching (Single WebView2) ==========

        private async Task SwitchToFileAsync(string fileName)
        {
            if (!_editorReady || !_fileContents.ContainsKey(fileName)) return;

            // Save current file content to memory
            if (_activeFileName != null)
            {
                var currentContent = await GetEditorContent();
                _fileContents[_activeFileName] = currentContent;
            }

            // Load new file
            _activeFileName = fileName;
            var content = _fileContents.GetValueOrDefault(fileName, "");
            var safeContent = JsonSerializer.Serialize(content);
            var safeFileName = JsonSerializer.Serialize(fileName);

            await EditorWebView.ExecuteScriptAsync($"setContent({safeContent})");
            await EditorWebView.ExecuteScriptAsync($"setFileName({safeFileName})");

            // Update UI indicators
            UpdateActiveIndicators(fileName);
        }

        private void UpdateActiveIndicators(string fileName)
        {
            bool isCoreScript = CoreScripts.Contains(fileName);

            // Update Core Script sidebar highlight
            foreach (var child in CoreScriptsPanel.Children.OfType<Button>())
            {
                bool isActive = child.Tag?.ToString() == fileName;
                child.Background = isActive ? new SolidColorBrush(Color.FromRgb(0x37, 0x37, 0x3D)) : Brushes.Transparent;
            }

            // Update Interceptor tabs
            if (isCoreScript)
            {
                // Deselect all interceptor tabs
                foreach (var tab in TabsPanel.Children.OfType<Border>())
                {
                    var sp = tab.Child as StackPanel;
                    var radio = sp?.Children.OfType<RadioButton>().FirstOrDefault();
                    if (radio != null) radio.IsChecked = false;
                }
            }
            else
            {
                // Deselect core sidebar
                foreach (var child in CoreScriptsPanel.Children.OfType<Button>())
                    child.Background = Brushes.Transparent;

                // Select matching interceptor tab
                foreach (var tabBorder in TabsPanel.Children.OfType<Border>())
                {
                    var sp = tabBorder.Child as StackPanel;
                    var radio = sp?.Children.OfType<RadioButton>().FirstOrDefault();
                    if (radio != null)
                        radio.IsChecked = radio.Tag?.ToString() == fileName;
                }
            }

            // Update title
            Title = $"Script Editor - {fileName}";
        }

        private async Task<string> GetEditorContent()
        {
            try
            {
                var result = await EditorWebView.ExecuteScriptAsync("getContent()");
                return JsonSerializer.Deserialize<string>(result) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // ========== Core Script Click ==========

        private void CoreScript_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;
            var fileName = btn.Tag.ToString()!;
            _ = SwitchToFileAsync(fileName);
        }

        // ========== Interceptor Tab Management ==========

        private void RefreshInterceptorList()
        {
            InterceptorListPanel.Children.Clear();
            TabsPanel.Children.Clear();

            var interceptorFiles = _fileContents.Keys
                .Where(f => !CoreScripts.Contains(f))
                .OrderBy(f => f)
                .ToList();

            foreach (var fileName in interceptorFiles)
            {
                AddInterceptorToSidebar(fileName);
                AddInterceptorTab(fileName);
            }

            UpdateTabStripVisibility();
        }

        private void UpdateTabStripVisibility()
        {
            TabStripBorder.Visibility = TabsPanel.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddInterceptorToSidebar(string fileName)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition {Width = new GridLength(1, GridUnitType.Star)});
            grid.ColumnDefinitions.Add(new ColumnDefinition {Width = GridLength.Auto});

            var itemBtn = new Button
            {
                Tag = fileName,
                Style = (Style) FindResource("SidebarItemStyle"),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "\uE943",
                            FontFamily = new FontFamily("Segoe MDL2 Assets"),
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA)),
                            Margin = new Thickness(0, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        new TextBlock {Text = Path.GetFileNameWithoutExtension(fileName)}
                    }
                }
            };
            itemBtn.Click += InterceptorSidebar_Click;
            Grid.SetColumn(itemBtn, 0);

            var deleteBtn = new Button
            {
                Tag = fileName,
                Content = "\u2715",
                Style = (Style) FindResource("DeleteBtnStyle")
            };
            deleteBtn.Click += DeleteInterceptor_Click;
            Grid.SetColumn(deleteBtn, 1);

            grid.Children.Add(itemBtn);
            grid.Children.Add(deleteBtn);

            // Show delete button on hover
            grid.MouseEnter += (s, e) => deleteBtn.Visibility = Visibility.Visible;
            grid.MouseLeave += (s, e) => deleteBtn.Visibility = Visibility.Hidden;

            InterceptorListPanel.Children.Add(grid);
        }

        private void AddInterceptorTab(string fileName)
        {
            var tabBorder = new Border
            {
                Tag = fileName,
                Background = Brushes.Transparent,
                Padding = new Thickness(0)
            };

            var sp = new StackPanel {Orientation = Orientation.Horizontal};

            var radio = new RadioButton
            {
                Content = Path.GetFileNameWithoutExtension(fileName),
                Tag = fileName,
                Style = (Style) FindResource("InterceptorTabStyle"),
                GroupName = "InterceptorTabs"
            };
            radio.Checked += InterceptorTab_Checked;

            var closeBtn = new Button
            {
                Content = "\u2715",
                Tag = fileName,
                Style = (Style) FindResource("TabCloseBtnStyle"),
                Margin = new Thickness(0, 0, 4, 0)
            };
            closeBtn.Click += TabClose_Click;

            sp.Children.Add(radio);
            sp.Children.Add(closeBtn);
            tabBorder.Child = sp;

            TabsPanel.Children.Add(tabBorder);
        }

        private void InterceptorSidebar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;
            var fileName = btn.Tag.ToString()!;

            // Also select the corresponding tab
            foreach (var tabBorder in TabsPanel.Children.OfType<Border>())
            {
                var sp = tabBorder.Child as StackPanel;
                var radio = sp?.Children.OfType<RadioButton>().FirstOrDefault();
                if (radio != null && radio.Tag?.ToString() == fileName)
                {
                    radio.IsChecked = true;
                    return;
                }
            }

            _ = SwitchToFileAsync(fileName);
        }

        private void InterceptorTab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton radio || radio.Tag == null) return;
            var fileName = radio.Tag.ToString()!;
            _ = SwitchToFileAsync(fileName);
        }

        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;
            var fileName = btn.Tag.ToString()!;

            // Remove tab only (don't delete file)
            var tabToRemove = TabsPanel.Children.OfType<Border>()
                .FirstOrDefault(b => b.Tag?.ToString() == fileName);
            if (tabToRemove != null)
                TabsPanel.Children.Remove(tabToRemove);
            UpdateTabStripVisibility();

            // If this was the active file, switch to another
            if (_activeFileName == fileName)
            {
                var firstTab = TabsPanel.Children.OfType<Border>().FirstOrDefault();
                if (firstTab != null)
                {
                    var sp = firstTab.Child as StackPanel;
                    var radio = sp?.Children.OfType<RadioButton>().FirstOrDefault();
                    if (radio != null)
                    {
                        radio.IsChecked = true;
                        return;
                    }
                }

                // Fall back to first core script
                _ = SwitchToFileAsync(CoreScripts[0]);
            }
        }

        // ========== Add Interceptor ==========

        private async void AddInterceptor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new InputNameDialog {Owner = this};
            if (dialog.ShowDialog() == true)
            {
                var name = dialog.ResponseText?.Trim();
                if (string.IsNullOrEmpty(name)) return;

                // Basic validation
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                {
                    MessageBox.Show("Invalid class name. Use alphanumeric characters, starting with a letter or underscore.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var fileName = $"{name}.cs";
                var filePath = Path.Combine(_workspacePath, fileName);

                if (File.Exists(filePath))
                {
                    MessageBox.Show($"File '{fileName}' already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    var template = ScriptTemplateFactory.GetTemplate("PacketInterceptor", name);
                    await File.WriteAllTextAsync(filePath, template);

                    _fileContents[fileName] = template;
                    _intelliSense?.UpdateDocument(fileName, template);

                    AddInterceptorToSidebar(fileName);
                    AddInterceptorTab(fileName);
                    UpdateTabStripVisibility();

                    // Switch to the new file
                    await SwitchToFileAsync(fileName);

                    // Select tab
                    foreach (var tabBorder in TabsPanel.Children.OfType<Border>())
                    {
                        var sp = tabBorder.Child as StackPanel;
                        var radio = sp?.Children.OfType<RadioButton>().FirstOrDefault();
                        if (radio != null && radio.Tag?.ToString() == fileName)
                        {
                            radio.IsChecked = true;
                            break;
                        }
                    }

                    AppendLog($"Created {fileName}", Brushes.Green);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to create file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }


        // ========== Delete Interceptor ==========

        private async void DeleteInterceptor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null)
                return;

            var fileName = btn.Tag.ToString()!;

            var result = MessageBox.Show($"Delete {fileName}?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            // Delete file from disk
            var filePath = Path.Combine(_workspacePath, fileName);
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    AppendLog($"Delete failed: {ex.Message}", Brushes.Red);
                    return;
                }
            }

            _fileContents.Remove(fileName);
            _dirtyFiles.Remove(fileName);

            // Refresh sidebar and tabs
            RefreshInterceptorList();

            // Switch to another file if this was active
            if (_activeFileName == fileName)
            {
                _activeFileName = null;

                var firstInterceptor = _fileContents.Keys.FirstOrDefault(f => !CoreScripts.Contains(f));
                await SwitchToFileAsync(firstInterceptor ?? CoreScripts[0]);
            }

            AppendLog($"Deleted {fileName}", Brushes.Orange);
        }

        // ========== Libs Section ==========

        private void RefreshLibsList()
        {
            LibsListPanel.Children.Clear();
            var libsDir = Path.Combine(_workspacePath, "Libs");
            if (!Directory.Exists(libsDir))
            {
                LibsHeaderText.Text = "LIBS (0)";
                return;
            }

            var dlls = Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories);
            LibsHeaderText.Text = $"LIBS ({dlls.Length})";

            foreach (var dll in dlls)
            {
                var tb = new TextBlock
                {
                    Text = Path.GetFileName(dll),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                    FontSize = 11,
                    Margin = new Thickness(4, 2, 0, 2)
                };
                LibsListPanel.Children.Add(tb);
            }
        }

        // ========== Save ==========

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            await SaveAllAsync();
        }

        private async Task SaveAllAsync()
        {
            // Save current editor content to memory first
            if (_activeFileName != null && _editorReady)
                _fileContents[_activeFileName] = await GetEditorContent();

            var savedCount = 0;
            foreach (var kvp in _fileContents)
            {
                var filePath = Path.Combine(_workspacePath, kvp.Key);
                await File.WriteAllTextAsync(filePath, kvp.Value);
                savedCount++;
            }

            _dirtyFiles.Clear();
            UpdateAllTabDirtyIndicators();
            AppendLog($"Saved {savedCount} files.", Brushes.Gray);
            SetStatus("Saved");
        }

        private void UpdateAllTabDirtyIndicators()
        {
            foreach (var tabBorder in TabsPanel.Children.OfType<Border>())
            {
                var sp = tabBorder.Child as StackPanel;
                var radio = sp?.Children.OfType<RadioButton>().FirstOrDefault();
                if (radio?.Tag == null) continue;

                var fileName = radio.Tag.ToString()!;
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                radio.Content = _dirtyFiles.Contains(fileName) ? $"{baseName} \u25cf" : baseName;
            }
        }

        // ========== Build ==========

        private async void BuildBtn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Building...";
            ScriptLogBox.Document.Blocks.Clear();
            BuildBtn.IsEnabled = false;

            try
            {
                await SaveAllAsync();
                AppendLog("Requesting Compilation...", Brushes.Gray);
                OnRequestCompilation?.Invoke();
            }
            catch (Exception ex)
            {
                AppendLog($"Build Error: {ex}", Brushes.Red);
                FileLogger.Instance.Error("BuildBtn_Click failed", ex);
                StatusText.Text = "Error";
            }
            finally
            {
                BuildBtn.IsEnabled = true;
            }
        }

        // ========== NuGet ==========

        public async Task RefreshEnvironmentAsync()
        {
            Dispatcher.Invoke(() => SetStatus("Refreshing..."));
            try
            {
                await InitializeIntelliSenseAsync();
                RefreshLibsList();
                RefreshInterceptorList();
                SetStatus("Ready");
            }
            catch (Exception ex)
            {
                AppendLog($"Refresh failed: {ex.Message}", Brushes.Orange);
                SetStatus("Error");
            }
        }

        private void PackagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_workspacePath)) return;
            var window = new NuGetWindow(_workspacePath, () => _ = RefreshEnvironmentAsync()) {Owner = this};
            window.ShowDialog();
        }

        // ========== Monaco Message Handler ==========

        private async Task HandleMonacoMessageAsync(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();
            var fileName = _activeFileName ?? "";

            switch (type)
            {
                case "requestCompletions":
                    await HandleCompletionRequestAsync(root, fileName);
                    break;
                case "requestSignatureHelp":
                    await HandleSignatureHelpRequestAsync(root, fileName);
                    break;
                case "requestDiagnostics":
                    await HandleDiagnosticsRequestAsync(root, fileName);
                    break;
                case "requestHover":
                    await HandleHoverRequestAsync(root, fileName);
                    break;
                case "requestDefinition":
                    await HandleDefinitionRequestAsync(root, fileName);
                    break;
                case "requestReferences":
                    await HandleReferencesRequestAsync(root, fileName);
                    break;
                case "requestCodeActions":
                    await HandleCodeActionsRequestAsync(root, fileName);
                    break;
                case "applyCodeAction":
                    await HandleApplyCodeActionAsync(root, fileName);
                    break;
                case "requestFormatting":
                    await HandleFormattingRequestAsync(root, fileName);
                    break;
                case "requestRename":
                    await HandleRenameRequestAsync(root, fileName);
                    break;
                case "requestSymbols":
                    await HandleSymbolsRequestAsync(root, fileName);
                    break;
                case "command":
                    HandleMonacoCommand(root);
                    break;
                case "contentChanged":
                    HandleMonacoContentChanged(root);
                    break;
                case "cursorPosition":
                    HandleCursorPosition(root);
                    break;
            }
        }

        // JSON Options for CamelCase (Critical for JS interop)
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private async Task HandleCompletionRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var completions = await _intelliSense.GetCompletionsAsync(fileName, position);
            var jsonResult = JsonSerializer.Serialize(completions, _jsonOptions);
            await EditorWebView.ExecuteScriptAsync($"setCompletions({jsonResult})");
        }

        private async Task HandleSignatureHelpRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var signatureHelp = await _intelliSense.GetSignatureHelpAsync(fileName, position);
            var jsonResult = signatureHelp != null ? JsonSerializer.Serialize(signatureHelp, _jsonOptions) : "null";
            await EditorWebView.ExecuteScriptAsync($"setSignatureHelp({jsonResult})");
        }

        private async Task HandleDiagnosticsRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var diagnostics = await _intelliSense.GetDiagnosticsAsync(fileName);
            var jsonResult = JsonSerializer.Serialize(diagnostics, _jsonOptions);
            await EditorWebView.ExecuteScriptAsync($"setDiagnostics({jsonResult})");

            // Update status bar diagnostics count
            Dispatcher.Invoke(() =>
            {
                var errors = diagnostics.Count(d => d.Severity == 8);
                var warnings = diagnostics.Count(d => d.Severity == 4);
                DiagnosticsText.Text = $"{errors}\u2715 {warnings}\u26A0";
            });
        }

        private async Task HandleHoverRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var hover = await _intelliSense.GetHoverAsync(fileName, position);
            var jsonResult = hover != null ? JsonSerializer.Serialize(hover, _jsonOptions) : "null";
            await EditorWebView.ExecuteScriptAsync($"setHover({jsonResult})");
        }

        private async Task HandleDefinitionRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var definition = await _intelliSense.GetDefinitionAsync(fileName, position);
            var jsonResult = definition != null ? JsonSerializer.Serialize(definition, _jsonOptions) : "null";
            await EditorWebView.ExecuteScriptAsync($"setDefinition({jsonResult})");
        }

        private async Task HandleReferencesRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var references = await _intelliSense.GetReferencesAsync(fileName, position);
            var jsonResult = JsonSerializer.Serialize(references, _jsonOptions);
            await EditorWebView.ExecuteScriptAsync($"setReferences({jsonResult})");
        }

        private async Task HandleCodeActionsRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var startPos = root.GetProperty("startPosition").GetInt32();
            var endPos = root.GetProperty("endPosition").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var actions = await _intelliSense.GetCodeActionsAsync(fileName, startPos, endPos);
            var jsonResult = JsonSerializer.Serialize(actions, _jsonOptions);
            await EditorWebView.ExecuteScriptAsync($"setCodeActions({jsonResult})");
        }

        private async Task HandleApplyCodeActionAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var actionIndex = root.GetProperty("actionIndex").GetInt32();
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var result = await _intelliSense.ApplyCodeActionAsync(fileName, actionIndex);
            if (result != null)
            {
                var safeContent = JsonSerializer.Serialize(result.NewContent, _jsonOptions);
                await EditorWebView.ExecuteScriptAsync($"setContent({safeContent})");
            }
        }

        private async Task HandleFormattingRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var edits = await _intelliSense.FormatDocumentAsync(fileName);
            var jsonResult = JsonSerializer.Serialize(edits, _jsonOptions);
            await EditorWebView.ExecuteScriptAsync($"setFormatting({jsonResult})");
        }

        private async Task HandleRenameRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var position = root.GetProperty("position").GetInt32();
            var newName = root.GetProperty("newName").GetString() ?? "";
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var result = await _intelliSense.RenameSymbolAsync(fileName, position, newName);
            var jsonResult = result != null ? JsonSerializer.Serialize(result) : "{ \"edits\": [] }";
            await EditorWebView.ExecuteScriptAsync($"setRenameEdits({jsonResult})");
        }

        private async Task HandleSymbolsRequestAsync(JsonElement root, string fileName)
        {
            if (_intelliSense == null) return;
            var content = root.GetProperty("content").GetString() ?? "";
            _intelliSense.UpdateDocument(fileName, content);
            var symbols = await _intelliSense.GetDocumentSymbolsAsync(fileName);
            var jsonResult = JsonSerializer.Serialize(symbols);
            await EditorWebView.ExecuteScriptAsync($"setDocumentSymbols({jsonResult})");
        }

        private void HandleMonacoCommand(JsonElement root)
        {
            var command = root.GetProperty("command").GetString();
            Dispatcher.Invoke(() =>
            {
                switch (command)
                {
                    case "save":
                        _ = SaveAllAsync();
                        break;
                    case "build":
                        BuildBtn_Click(this, new RoutedEventArgs());
                        break;
                }
            });
        }

        private void HandleMonacoContentChanged(JsonElement root)
        {
            if (_activeFileName == null) return;
            Dispatcher.Invoke(() => MarkDirty(_activeFileName));
        }

        private void HandleCursorPosition(JsonElement root)
        {
            if (root.TryGetProperty("line", out var lineProp) && root.TryGetProperty("column", out var colProp))
            {
                var line = lineProp.GetInt32();
                var col = colProp.GetInt32();
                Dispatcher.Invoke(() => CursorPositionText.Text = $"Ln {line}, Col {col}");
            }
        }

        // ========== Dirty State ==========

        private void MarkDirty(string fileName)
        {
            if (!_dirtyFiles.Add(fileName)) return;

            // Update tab indicator
            foreach (var tabBorder in TabsPanel.Children.OfType<Border>())
            {
                var sp = tabBorder.Child as StackPanel;
                var radio = sp?.Children.OfType<RadioButton>().FirstOrDefault();
                if (radio?.Tag?.ToString() == fileName)
                {
                    radio.Content = Path.GetFileNameWithoutExtension(fileName) + " \u25cf";
                    break;
                }
            }
        }

        // ========== Lifecycle ==========

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                EditorWebView.Dispose();
            }
            catch
            {
            }
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
}