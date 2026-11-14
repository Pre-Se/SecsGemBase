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

    /// <summary>Writes one HSMS frame to the current connection.</summary>
    /// <returns>false if there is no connection to write to.</returns>
    /// <exception cref="IOException">Writing failed, e.g. the remote side has reset the connection; also
    /// <see cref="System.Net.Sockets.SocketException"/>, <see cref="InvalidOperationException"/> or
    /// <see cref="ObjectDisposedException"/> when the connection has just ended.</exception>
    /// <exception cref="OperationCanceledException">Dispose() of this object has started, also for a write that it
    /// interrupts; TCPIPClientBase and TCPIPServerBase do this. CommunicationHandler reports the message as failed and,
    /// unlike for the other failures, requests no restart: whoever disposes the object is already replacing or closing
    /// the connection.</exception>
    Task<bool> SendData(ReadOnlyMemory<byte> data);
    public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; }
}