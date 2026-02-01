using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace ProtoTestTool
{
    public partial class App : Application
    {
        private Mutex? _workspaceMutex;

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
                if (!TryAcquireWorkspaceLock(workspaceDialog.SelectedPath))
                {
                    MessageBox.Show(
                        "이미 다른 ProtoTestTool에서 사용 중인 워크스페이스입니다.",
                        "워크스페이스 잠금",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }

                var mainWindow = new MainWindow(workspaceDialog.SelectedPath);
                Current.MainWindow = mainWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
            }
            else
            {
                Shutdown();
            }
        }

        public bool TryAcquireWorkspaceLock(string workspacePath)
        {
            var mutexName = $"Global\\ProtoTestTool_{ComputeHash(workspacePath)}";
            var mutex = new Mutex(true, mutexName, out var createdNew);

            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
    
            // Release previous workspace lock
            ReleaseWorkspaceLock();
            _workspaceMutex = mutex;
            return true;
        }

        public void ReleaseWorkspaceLock()
        {
            if (_workspaceMutex == null) return;

            try
            {
                _workspaceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned
            }

            _workspaceMutex.Dispose();
            _workspaceMutex = null;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ReleaseWorkspaceLock();
            base.OnExit(e);
        }

        private static string ComputeHash(string path)
        {
            var normalized = path.TrimEnd('\\', '/').ToUpperInvariant();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes)[..16];
        }
    }
}
