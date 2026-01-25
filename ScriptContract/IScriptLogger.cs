namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Interface for logging from scripts to the tool's output console.
    /// </summary>
    public interface IScriptLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }
}
