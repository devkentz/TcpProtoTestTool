using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ProtoTestTool.Services;

public class VsCodeServerManager : IDisposable
{
    private Process? _serverProcess;
    private string? _vsCodeCmdPath;
    private readonly TaskCompletionSource<bool> _serverReady = new();
    private bool _disposed;

    public bool IsVsCodeInstalled => DetectVsCodePath() != null;
    public bool IsRunning => _serverProcess is { HasExited: false };
    public int? Port { get; private set; }

    public event Action<string>? OutputReceived;
    public event Action? ServerReady;
    public event Action? ServerStopped;

    public string? DetectVsCodePath()
    {
        if (_vsCodeCmdPath != null)
            return _vsCodeCmdPath;

        // 1. User-installed VS Code (typical)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userPath = Path.Combine(localAppData, "Programs", "Microsoft VS Code", "bin", "code.cmd");
        if (File.Exists(userPath))
        {
            _vsCodeCmdPath = userPath;
            return _vsCodeCmdPath;
        }

        // 2. System-installed VS Code
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var systemPath = Path.Combine(programFiles, "Microsoft VS Code", "bin", "code.cmd");
        if (File.Exists(systemPath))
        {
            _vsCodeCmdPath = systemPath;
            return _vsCodeCmdPath;
        }

        // 3. Program Files (x86)
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var x86Path = Path.Combine(programFilesX86, "Microsoft VS Code", "bin", "code.cmd");
        if (File.Exists(x86Path))
        {
            _vsCodeCmdPath = x86Path;
            return _vsCodeCmdPath;
        }

        // 4. Search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "code.cmd");
            if (File.Exists(candidate))
            {
                _vsCodeCmdPath = candidate;
                return _vsCodeCmdPath;
            }
        }

        return null;
    }

    public async Task<int> StartServerAsync(string workspaceFolderPath, CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("VS Code server is already running.");

        var codePath = DetectVsCodePath()
            ?? throw new FileNotFoundException("VS Code is not installed. Install VS Code to use the full IDE.");

        var port = FindFreePort();
        Port = port;

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{codePath}\" serve-web --port {port} --without-connection-token --accept-server-license-terms\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workspaceFolderPath
        };

        _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _serverProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            OutputReceived?.Invoke(e.Data);

            if (e.Data.Contains("Web UI available at") || e.Data.Contains("http://localhost") || e.Data.Contains($"http://127.0.0.1:{port}"))
            {
                _serverReady.TrySetResult(true);
                ServerReady?.Invoke();
            }
        };

        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            OutputReceived?.Invoke($"[stderr] {e.Data}");

            // serve-web sometimes outputs the ready URL to stderr
            if (e.Data.Contains("Web UI available at") || e.Data.Contains($"http://127.0.0.1:{port}"))
            {
                _serverReady.TrySetResult(true);
                ServerReady?.Invoke();
            }
        };

        _serverProcess.Exited += (_, _) =>
        {
            Port = null;
            _serverReady.TrySetResult(false);
            ServerStopped?.Invoke();
        };

        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        // Wait for server ready or timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var ready = await WaitForServerReadyAsync(port, timeoutCts.Token);
            if (!ready)
                throw new TimeoutException("VS Code server did not start in time.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("VS Code server did not start in time.");
        }

        return port;
    }

    public void StopServer()
    {
        if (_serverProcess == null) return;

        try
        {
            if (!_serverProcess.HasExited)
            {
                // Kill the process tree (cmd.exe + child processes)
                KillProcessTree(_serverProcess.Id);
            }
        }
        catch
        {
            // Ignore errors during shutdown
        }
        finally
        {
            _serverProcess.Dispose();
            _serverProcess = null;
            Port = null;
        }
    }

    private async Task<bool> WaitForServerReadyAsync(int port, CancellationToken ct)
    {
        // Poll the server with HTTP GET until it responds
        using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}", ct);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.OK
                    || (int)response.StatusCode < 500)
                {
                    return true;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Server not ready yet
            }

            await Task.Delay(500, ct);
        }

        return false;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void KillProcessTree(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch
        {
            // Best effort
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
        GC.SuppressFinalize(this);
    }
}
