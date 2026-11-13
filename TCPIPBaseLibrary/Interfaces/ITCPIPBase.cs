using SecsGemHelperClasses.Interfaces;

namespace TCPIPBaseLibrary.Interfaces;

public interface ITCPIPBase : IDisposable
{
    /// <summary>Raised on the connection's loop once a connection is established, before its first message is read.</summary>
    event EventHandler OnConnect;

    /// <summary>Raised once when <see cref="Connect"/> ends, after a lost connection as well as after Dispose().</summary>
    event EventHandler OnDisconnected;

    /// <summary>
    /// Serves one connection: waits for or retries the connection until it is established, receives until it ends,
    /// and then completes. Reconnecting is up to the owner (CommunicationHandler creates a new object).
    /// </summary>
    Task Connect();
    Task<bool> SendData(ReadOnlyMemory<byte> data);
    public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; }
}