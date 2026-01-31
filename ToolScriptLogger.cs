using System.Windows.Media;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool;

public class ToolScriptLogger : IScriptLogger
{
    private readonly Action<string, SolidColorBrush> _logAction;

    public ToolScriptLogger(Action<string, SolidColorBrush> logAction)
    {
        _logAction = logAction;
    }

    public void Info(string message) => _logAction($"[INFO] {message}", Brushes.White);
    public void Warn(string message) => _logAction($"[WARN] {message}", Brushes.Orange);
    public void Error(string message) => _logAction($"[ERROR] {message}", Brushes.Red);
}