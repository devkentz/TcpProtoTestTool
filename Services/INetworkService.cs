using System;
using System.Threading.Tasks;

namespace ProtoTestTool.Services
{
    /// <summary>
    /// Defines the networking operations for the Test Client mode.
    /// </summary>
    public interface INetworkService
    {
        event Action? Connected;
        event Action? Disconnected;
        event Action<string>? ErrorOccurred;
        event Action<byte[]>? DataReceived;

        bool IsConnected { get; }

        Task ConnectAsync(string ip, int port);
        void Disconnect();
        
        Task SendAsync(ReadOnlyMemory<byte> data);
    }
}
