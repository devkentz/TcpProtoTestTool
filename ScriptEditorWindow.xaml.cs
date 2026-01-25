using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace ProtoTestTool
{
    public partial class ScriptEditorWindow : Wpf.Ui.Controls.UiWindow
    {
        private readonly string _workspacePath;
        private readonly ScriptLoader _scriptLoader;
        
        // Callback when compilation succeeds


        public ScriptEditorWindow(string workspacePath, ScriptLoader scriptLoader)
        {
            InitializeComponent();
            _workspacePath = workspacePath;
            _scriptLoader = scriptLoader;

            Loaded += ScriptEditorWindow_Loaded;
        }

        private async void ScriptEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeEditorsAsync();
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

            // Initialize WebView2 for each tab
            await InitializeWebView(RegistryEditor, editorUrl, "PacketRegistry.csx");
            await InitializeWebView(HeaderEditor, editorUrl, "PacketHeader.csx");
            await InitializeWebView(SerializerEditor, editorUrl, "PacketSerializer.csx");
            await InitializeWebView(ContextEditor, editorUrl, "PacketHandler.csx");
            
            SetStatus("Ready");
        }

        private async Task InitializeWebView(Microsoft.Web.WebView2.Wpf.WebView2 webView, string url, string fileName)
        {
            try
            {
                // Handshake Mechanism
                var tcs = new TaskCompletionSource<bool>();
                
                webView.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        // Check JSON property directly for object messages
                        var json = e.WebMessageAsJson;
                        if (!string.IsNullOrEmpty(json))
                        {
                             if (json.Contains("\"type\":\"ready\"") || json.Contains("\"ready\""))
                             {
                                 tcs.TrySetResult(true);
                             }
                             else
                             {
                                 // Log other messages (extensions/debug)
                                 AppendLog($"[Editor JS] {json}", Brushes.Cyan);
                             }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[Editor Msg Error] {ex.Message}", Brushes.Red);
                    }
                };

                webView.Source = new Uri(url);

                // Wait for ready signal (timeout 3s)
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
                if (completedTask == tcs.Task)
                {
                     await LoadFileIntoEditor(webView, fileName);
                }
                else
                {
                     AppendLog($"[Editor] Timeout waiting for editor ready: {fileName}", Brushes.Orange);
                     // Fallback try
                     await LoadFileIntoEditor(webView, fileName);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to initialize editor for {fileName}: {ex.Message}", Brushes.Red);
            }
        }

        private async Task LoadFileIntoEditor(Microsoft.Web.WebView2.Wpf.WebView2 webView, string fileName)
        {
            var path = Path.Combine(_workspacePath, fileName);
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                // Escape for JS
                var safeContent = System.Text.Json.JsonSerializer.Serialize(content);
                await webView.ExecuteScriptAsync($"setContent({safeContent})");
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (RegistryEditor == null) return; // Not initialized yet

            var button = sender as System.Windows.Controls.Primitives.ToggleButton;
            if (button == null || button.Tag == null) return;

            string targetName = button.Tag.ToString()!;
            
            // Hide all (Collapse/Hidden)
            // Note: We use Hidden initially for loading, but switching to Collapsed/Visible logic here
            // To ensure state is kept, we just show one.
            // Wait, if we use Collapsed, does it un-initialize? 
            // In Grid, Collapsed just removes from layout. It should be fine.
            
            SetVisibility(RegistryEditor, targetName == "RegistryEditor");
            SetVisibility(HeaderEditor, targetName == "HeaderEditor");
            SetVisibility(SerializerEditor, targetName == "SerializerEditor");
            SetVisibility(ContextEditor, targetName == "ContextEditor");
        }

        private void SetVisibility(Microsoft.Web.WebView2.Wpf.WebView2 webView, bool isVisible)
        {
            if (webView == null) return;
            // Use Visibility.Hidden to keep layout/rendering state or Collapsed?
            // Collapsed removes it from layout space. Hidden keeps space.
            // Since they are in the same Grid cell (overlapping), Hidden is fine if we want them to stack?
            // No, Hidden takes space. Does Grid center them?
            // They are in Grid Row 1. If Hidden, they take space.
            // If Collapsed, they don't.
            // Let's use Collapsed for the inactive ones.
            webView.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task<string> GetEditorContent(Microsoft.Web.WebView2.Wpf.WebView2 webView)
        {
            try 
            {
                var result = await webView.ExecuteScriptAsync("getContent()");
                // Result is JSON encoded string, need to deserialize
                return System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppendLog($"Error getting content: {ex.Message}", Brushes.Red);
                return string.Empty;
            }
        }

        private async void CompileScriptBtn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Compiling...";
            ScriptLogBox.Text = "";
            CompileScriptBtn.IsEnabled = false;

            try
            {
                // 1. Get content from all editors asynchronously
                var registryCode = await GetEditorContent(RegistryEditor);
                var headerCode = await GetEditorContent(HeaderEditor);
                var serializerCode = await GetEditorContent(SerializerEditor);
                var handlerCode = await GetEditorContent(ContextEditor);

                // Validation: Prevent overwriting with empty content if load failed
                if (string.IsNullOrWhiteSpace(registryCode) || string.IsNullOrWhiteSpace(headerCode) || 
                    string.IsNullOrWhiteSpace(serializerCode) || string.IsNullOrWhiteSpace(handlerCode))
                {
                    AppendLog("Error: One or more editors are empty. Aborting save to prevent data loss.", Brushes.Red);
                    
                    // Allow compilation of existing files? Or Stop?
                    // Better to stop and ask user to reload/check.
                    StatusText.Text = "Save Aborted";
                    return; 
                }

                // 2. Save all files
                await File.WriteAllTextAsync(Path.Combine(_workspacePath, "PacketRegistry.csx"), registryCode);
                await File.WriteAllTextAsync(Path.Combine(_workspacePath, "PacketHeader.csx"), headerCode);
                await File.WriteAllTextAsync(Path.Combine(_workspacePath, "PacketSerializer.csx"), serializerCode);
                await File.WriteAllTextAsync(Path.Combine(_workspacePath, "PacketHandler.csx"), handlerCode);

                AppendLog("Files Saved. Starting Compilation...", Brushes.Gray);

                // 3. Request Compilation
                OnRequestCompilation?.Invoke(); 

                // 4. Update Completions (if successful, types will be in ScriptGlobals or ProtoLoaderManager)
                // We'll give it a small delay or call this explicitly after compilation finishes in MainWindow
            }
            catch (Exception ex)
            {
                AppendLog($"Error: {ex.Message}", Brushes.Red);
                StatusText.Text = "Error";
            }
            finally
            {
                CompileScriptBtn.IsEnabled = true;
            }
        }

        public async Task UpdateCompletionsAsync(string json)
        {
             await RegistryEditor.ExecuteScriptAsync($"updateCompletions({json})");
             await HeaderEditor.ExecuteScriptAsync($"updateCompletions({json})");
             await SerializerEditor.ExecuteScriptAsync($"updateCompletions({json})");
             await ContextEditor.ExecuteScriptAsync($"updateCompletions({json})");
        }
        
        public event Action? OnRequestCompilation;

        public void AppendLog(string message, Brush color)
        {
            ScriptLogBox.Text += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
            ScriptLogBox.ScrollToEnd();
        }
        
        public void SetStatus(string status) => StatusText.Text = status;
    }
}
