using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProtoTestTool
{
    public partial class NuGetWindow : Wpf.Ui.Controls.UiWindow
    {
        private readonly string _workspacePath;
        private readonly NuGetClient _client;

        public NuGetWindow(string workspacePath)
        {
            InitializeComponent();
            _workspacePath = workspacePath;
            _client = new NuGetClient();
        }

        private async void SearchBtn_Click(object sender, RoutedEventArgs e)
        {
            await PerformSearch();
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await PerformSearch();
            }
        }

        private async Task PerformSearch()
        {
            var query = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            SearchBtn.IsEnabled = false;
            ResultList.ItemsSource = null;
            StatusText.Text = "Searching...";

            try
            {
                var results = await _client.SearchAsync(query);
                ResultList.ItemsSource = results;
                StatusText.Text = $"Found {results.Count} results.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                SearchBtn.IsEnabled = true;
            }
        }

        private async void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NuGetPackageInfo pkg)
            {
                btn.IsEnabled = false;
                StatusText.Text = $"Installing {pkg.Id} v{pkg.Version}...";
                
                try
                {
                    await _client.InstallPackageAsync(pkg.Id, pkg.Version, _workspacePath);
                    StatusText.Text = $"Successfully installed {pkg.Id}. References updated.";
                    
                    System.Windows.MessageBox.Show($"Installed {pkg.Id} to Libs folder.\nThe tool will auto-reference it on next compilation.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Install Failed: {ex.Message}";
                    FluentMessageBox.ShowError($"Failed to install {pkg.Id}:\n{ex.Message}");
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            }
        }
    }
}
