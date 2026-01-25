using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Collections.Generic;

namespace ProtoTestTool
{
    public partial class WorkspaceDialog : Wpf.Ui.Controls.UiWindow
    {
        public string? SelectedPath { get; private set; }
        private GlobalSettings _settings;

        public class RecentItem
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
        }

        public WorkspaceDialog(string? initialPath)
        {
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
                .Where(Directory.Exists) // Filter out missing folders
                .Select(p => new RecentItem 
                { 
                    Name = new DirectoryInfo(p).Name, 
                    Path = p 
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
                SelectWorkspace(dialog.FolderName);
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

        private void ExitBtn_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
