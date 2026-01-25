namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// API for Test Client Mode operations.
    /// Allows sending messages and controlling client behavior.
    /// </summary>
    public interface IClientApi
    {
        /// <summary>
        /// Sends a message to the server.
        /// </summary>
        /// <typeparam name="TMessage">The type of the message object.</typeparam>
        /// <param name="message">The message object to send.</param>
        void Send<TMessage>(TMessage message);

        /// <summary>
        /// Delays execution for a specified duration.
        /// </summary>
        /// <param name="milliseconds">Time to wait in milliseconds.</param>
        void Delay(int milliseconds);
    }
}
