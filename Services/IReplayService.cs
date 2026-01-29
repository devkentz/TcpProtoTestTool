    public interface IReplayService
    {
        Task<List<RecordedPacket>> LoadRecordingAsync(string filePath);
        Task ReplayAllAsync(List<RecordedPacket> packets, Func<Google.Protobuf.IMessage, object, Task> sendCallback, Action<string, Brush> logger);
    }
}
