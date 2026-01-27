using System.Windows;

namespace ProtoTestTool
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {

        }
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Prevent shutdown when WorkspaceDialog closes
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var workspaceDialog = new WorkspaceDialog();
            if (workspaceDialog.ShowDialog() == true && !string.IsNullOrEmpty(workspaceDialog.SelectedPath))
            {
                var mainWindow = new MainWindow(workspaceDialog.SelectedPath);
                Application.Current.MainWindow = mainWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
            }
            else
            {
                Shutdown();
            }
        }
    }
}