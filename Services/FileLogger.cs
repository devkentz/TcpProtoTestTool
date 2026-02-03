using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace ProtoTestTool.Services
{
    public sealed class FileLogger
    {
        public static FileLogger Instance { get; } = new();

        private ILogger _logger = Serilog.Core.Logger.None;

        private FileLogger() { }

        public void Init(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath)) return;

            var logsDir = Path.Combine(workspacePath, "logs");
            Directory.CreateDirectory(logsDir);
            var logFilePath = Path.Combine(logsDir, "tool.log");

            _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    logFilePath,
                    rollingInterval: RollingInterval.Infinite,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    retainedFileCountLimit: 1,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u4}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        public void Info(string message) => _logger.Information(message);

        public void Warn(string message) => _logger.Warning(message);

        public void Error(string message, Exception? ex = null)
        {
            if (ex != null)
                _logger.Error(ex, message);
            else
                _logger.Error(message);
        }
    }
}
