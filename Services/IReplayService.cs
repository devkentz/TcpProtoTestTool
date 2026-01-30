using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using ProtoTestTool.Network; // Assuming RecordedPacket is here or in Services? 
// RecordedPacket is in ProtoTestTool.Services namespace in PacketRecorder.cs.
// Since IReplayService is in ProtoTestTool.Services, no need for extra using if RecordedPacket is in same namespace.
// BUT PacketRecorder.cs defined public class RecordedPacket inside ProtoTestTool.Services.
// So it matches on namespace.

namespace ProtoTestTool.Services
{
    public interface IReplayService
    {
        Task<List<RecordedPacket>> LoadRecordingAsync(string filePath);
        Task ReplayAllAsync(List<RecordedPacket> packets, Func<Google.Protobuf.IMessage, object?, Task> sendCallback, Action<string, Brush> logger);
    }
}
