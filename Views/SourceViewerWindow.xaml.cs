using System.IO;
using System.Windows;

namespace ProtoTestTool.Views
{
    public partial class SourceViewerWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly string _content;
        private readonly string _language;

        public SourceViewerWindow(string title, string content, string language = "proto")
        {
            InitializeComponent();
            Title = title;
            _content = content;
            _language = language;

            Loaded += SourceViewerWindow_Loaded;
        }

        private async void SourceViewerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeEditorAsync();
        }

        private async Task InitializeEditorAsync()
        {
            try
            {
                var editorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "editor.html");
                if (!File.Exists(editorPath))
                {
                    MessageBox.Show($"Editor file not found: {editorPath}");
                    return;
                }

                await EditorView.EnsureCoreWebView2Async();
                EditorView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                EditorView.CoreWebView2.Settings.IsScriptEnabled = true;

                EditorView.Source = new Uri(editorPath);
                
                EditorView.NavigationCompleted += async (s, e) =>
                {
                    if (e.IsSuccess)
                    {
                        // Set Theme and Language
                        await EditorView.ExecuteScriptAsync("setTheme('vs-dark');");
                        await EditorView.ExecuteScriptAsync($"setLanguage('{_language}');");

                        // Set Content
                        var jsonContent = System.Text.Json.JsonSerializer.Serialize(_content);
                        await EditorView.ExecuteScriptAsync($"setContent({jsonContent});");
                    }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to init editor: {ex.Message}");
            }
        }
    }
}
