using SecsGemHelperClasses.Interfaces;

namespace TCPIPBaseLibrary.Interfaces;

public interface ITCPIPBase : IDisposable
{
    event EventHandler OnConnect;
    event EventHandler OnDisconnected;
    Task Connect();
    Task<bool> SendData(ReadOnlyMemory<byte> data);
    public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; }
}