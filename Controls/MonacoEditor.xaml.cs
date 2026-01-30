using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json;
using Microsoft.Web.WebView2.Core;

namespace ProtoTestTool.Controls
{
    public partial class MonacoEditor : UserControl
    {
        public event EventHandler? EditorReady;


        public static readonly DependencyProperty EditorLanguageProperty =
            DependencyProperty.Register("EditorLanguage", typeof(string), typeof(MonacoEditor), new PropertyMetadata("json", OnPropertyChanged));

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register("IsReadOnly", typeof(bool), typeof(MonacoEditor), new PropertyMetadata(false, OnPropertyChanged));

        public static readonly DependencyProperty ShowMinimapProperty =
            DependencyProperty.Register("ShowMinimap", typeof(bool), typeof(MonacoEditor), new PropertyMetadata(true, OnPropertyChanged));

        public string EditorLanguage
        {
            get { return (string)GetValue(EditorLanguageProperty); }
            set { SetValue(EditorLanguageProperty, value); }
        }

        public bool IsReadOnly
        {
            get { return (bool)GetValue(IsReadOnlyProperty); }
            set { SetValue(IsReadOnlyProperty, value); }
        }

        public bool ShowMinimap
        {
            get { return (bool)GetValue(ShowMinimapProperty); }
            set { SetValue(ShowMinimapProperty, value); }
        }

        private bool _isReady = false;
        private string? _pendingContent = null;


        public MonacoEditor()
        {
            InitializeComponent();
            Loaded += MonacoEditor_Loaded;
        }

        private async void MonacoEditor_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeAsync();
        }

        private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MonacoEditor editor && editor._isReady)
            {
                if (e.Property.Name == "EditorLanguage") _ = editor.UpdateLanguage((string)e.NewValue);
                if (e.Property.Name == "IsReadOnly") _ = editor.UpdateReadOnly((bool)e.NewValue);
                if (e.Property.Name == "ShowMinimap") _ = editor.UpdateMinimap((bool)e.NewValue);
            }
        }

        public async Task InitializeAsync()
        {
            if (_isReady) return;

            try
            {
                var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
                if (!File.Exists(editorPath))
                {
                    LoadingOverlay.Visibility = Visibility.Visible;
                    ((TextBlock)((StackPanel)LoadingOverlay.Child).Children[0]).Text = "Editor file missing";
                    return;
                }

                await EditorWebView.EnsureCoreWebView2Async();
                EditorWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                EditorWebView.CoreWebView2.Settings.IsScriptEnabled = true;

                var tcs = new TaskCompletionSource<bool>();
                
                EventHandler<CoreWebView2WebMessageReceivedEventArgs>? msgHandler = null;
                msgHandler = (s, e) =>
                {
                    try
                    {
                        var json = e.WebMessageAsJson;
                        // Simple check for ready signal (depends on editor.html impl)
                        if (!string.IsNullOrEmpty(json) && (json.Contains("\"type\":\"ready\"") || json.Contains("\"ready\"")))
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                    catch { }
                };

                EditorWebView.CoreWebView2.WebMessageReceived += msgHandler;
                EditorWebView.CoreWebView2.Navigate(new Uri(editorPath).AbsoluteUri);

                // Wait for ready with timeout
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                EditorWebView.CoreWebView2.WebMessageReceived -= msgHandler;

                if (completedTask == tcs.Task && tcs.Task.Result)
                {
                    _isReady = true;
                    LoadingOverlay.Visibility = Visibility.Collapsed;

                    // Apply Initial Settings
                    await UpdateLanguage(EditorLanguage);

                    await UpdateReadOnly(IsReadOnly);
                    await UpdateMinimap(ShowMinimap);
                    await EditorWebView.ExecuteScriptAsync("setTheme('vs-dark');");

                    if (_pendingContent != null)
                    {
                        await SetTextAsync(_pendingContent);
                        _pendingContent = null;
                    }

                    EditorReady?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // Timeout or failure
                     ((TextBlock)((StackPanel)LoadingOverlay.Child).Children[0]).Text = "Editor Timeout";
                }
            }
            catch (Exception ex)
            {
                 ((TextBlock)((StackPanel)LoadingOverlay.Child).Children[0]).Text = $"Init Error: {ex.Message}";
            }
        }

        public async Task SetTextAsync(string content)
        {
            if (!_isReady)
            {
                _pendingContent = content;
                return;
            }
             
            if (EditorWebView.CoreWebView2 == null) return;
            var jsonContent = JsonConvert.SerializeObject(content);
            await EditorWebView.ExecuteScriptAsync($"setContent({jsonContent});");
        }

        public async Task<string> GetTextAsync()
        {
            if (!_isReady || EditorWebView.CoreWebView2 == null) return string.Empty;
            
            try 
            {
                var result = await EditorWebView.ExecuteScriptAsync("getContent();");
                if (result == "null" || result == "undefined") return string.Empty;
                return JsonConvert.DeserializeObject<string>(result) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task UpdateLanguage(string lang)
        {
            if (!_isReady) return;
            await EditorWebView.ExecuteScriptAsync($"setLanguage('{lang}');");
        }

        private async Task UpdateReadOnly(bool readOnly)
        {
            if (!_isReady) return;
            await EditorWebView.ExecuteScriptAsync($"setReadOnly({readOnly.ToString().ToLower()});");
        }

        private async Task UpdateMinimap(bool show)
        {
            if (!_isReady) return;
            await EditorWebView.ExecuteScriptAsync($"setMinimap({show.ToString().ToLower()});");
        }
    }
}
