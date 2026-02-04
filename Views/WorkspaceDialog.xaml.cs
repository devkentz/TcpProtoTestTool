using System.IO;
using System.Windows;
using ProtoTestTool.Models;
using ProtoTestTool.Services;

namespace ProtoTestTool.Views
{
    public partial class WorkspaceDialog : Wpf.Ui.Controls.FluentWindow
    {
        public string? SelectedPath { get; private set; }
        private readonly GlobalSettings _settings;

        public class RecentItem
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public bool IsCurrent { get; set; }
        }

        private readonly string? _currentWorkspacePath;

        public WorkspaceDialog(string? initialPath)
        {
            _currentWorkspacePath = initialPath;
            InitializeComponent();
            _settings = GlobalSettings.Load();
            LoadRecentList();
            Activate();
            Focus();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
        
        // Default constructor required for XAML
        public WorkspaceDialog() : this(null) { }

        private void LoadRecentList()
        {
            var items = _settings.RecentWorkspaces
                .Where(Directory.Exists)
                .Select(p => new RecentItem
                {
                    Name = new DirectoryInfo(p).Name,
                    Path = p,
                    IsCurrent = string.Equals(p, _currentWorkspacePath, StringComparison.OrdinalIgnoreCase)
                })
                .ToList();

            RecentListBox.ItemsSource = items;
        }

        private void SelectWorkspace(string path)
        {
            if (Directory.Exists(path))
            {
                SelectedPath = path;
                _settings.AddRecent(path); // Update Recent List (move to top)
                DialogResult = true;
                Close();
            }
            else
            {
                FluentMessageBox.ShowError($"Folder not found: {path}");
                
                _settings.RemoveRecent(path);
                LoadRecentList();
            }
        }

        private void NewBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select New Workspace Folder (Empty Folder Recommended)"
            };

            if (dialog.ShowDialog() == true)
            {
                var path = dialog.FolderName;
                if (!Directory.Exists(path) || Directory.GetFileSystemEntries(path).Length == 0)
                {
                     _ = InitializeAndSelectAsync(path);
                }
                else
                {
                    SelectWorkspace(path);
                }
            }
        }

        private async Task InitializeAndSelectAsync(string path)
        {
             try
             {
                 var scaffolder = new ScaffoldingService();
                 await scaffolder.InitializeWorkspaceAsync(path);
                 SelectWorkspace(path);
             }
             catch (Exception ex)
             {
                 FluentMessageBox.ShowError($"Failed to initialize workspace: {ex.Message}");
             }
        }

        private void OpenBtn_Click(object sender, RoutedEventArgs e)
        {
             var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Open Existing Workspace"
            };

            if (dialog.ShowDialog() == true)
            {
                SelectWorkspace(dialog.FolderName);
            }
        }

        private void RecentListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (RecentListBox.SelectedItem is RecentItem item)
            {
                SelectWorkspace(item.Path);
            }
        }

        private void RecentListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && RecentListBox.SelectedItem is RecentItem item)
            {
                SelectWorkspace(item.Path);
            }
        }

        private void DeleteItemBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string path)
                return;

            var result = FluentMessageBox.ShowConfirm(
                "Remove Workspace",
                $"Do you also want to delete the workspace folder from disk?\n\n{path}\n\n" +
                "Yes = Remove from list AND delete from disk\n" +
                "No = Remove from list only");

            if (result == MessageBoxResult.Cancel)
                return;

            _settings.RemoveRecent(path);

            if (result == MessageBoxResult.Yes && Directory.Exists(path))
            {
                try
                {
                    // If deleting current workspace, UNLOAD it first
                    if (string.Equals(path, _currentWorkspacePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Owner is MainWindow mw)
                        {
                            mw.UnloadCurrentWorkspace();
                        }
                        
                        if (Application.Current is App app)
                        {
                            app.ReleaseWorkspaceLock();
                        }
                    }

                    Directory.Delete(path, true);
                }
                catch (UnauthorizedAccessException)
                {
                    FluentMessageBox.ShowError($"Access denied. Try running as Administrator or check folder permissions.\n\n{path}");
                }
                catch (IOException ex)
                {
                    FluentMessageBox.ShowError($"Cannot delete folder. It may be in use by another process.\n\n{ex.Message}");
                }
                catch (Exception ex)
                {
                    FluentMessageBox.ShowError($"Failed to delete folder: {ex.Message}");
                }
            }

            LoadRecentList();
        }

        private void ExitBtn_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
