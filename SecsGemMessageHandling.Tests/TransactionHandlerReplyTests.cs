using Logging;
using Logging.Enums;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemBaseItems.SecsGemParameters.Enums;
using SecsGemHelperClasses;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Helpers;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using Xunit;

namespace SecsGemMessageHandling.Tests;

/// <summary>
/// A reply for a transaction that is already completed or cancelled is ignored with a warning. Until GitLab
/// secsgem/secsgembase#12 TryHandleDataMessageReply called SetResult, which threw; it runs as a Subscribe(Action) handler
/// on the event bus, so that ended the process.
/// </summary>
public class TransactionHandlerReplyTests : IDisposable
{
    private const uint SystemBytes = 0x1234;
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);
    private readonly RecordingLogger<TransactionHandler> log = new();
    private readonly CommunicationHandler communication;
    private readonly TransactionHandler transactions;

    public TransactionHandlerReplyTests()
    {
        var parameters = new HSMSParameters();
        communication = new CommunicationHandler(new NetworkSettings { ConnectionMode = ConnectionMode.Active },
            new UnusedConnectionFactory(), NullLogger<CommunicationHandler>.Instance, parameters,
            new ControlMessageFactory(parameters), EventBuses);
        transactions = new TransactionHandler(parameters, log, communication, EventBuses);
    }

    public void Dispose() => communication.Dispose();

    /// <summary>
    /// The remote side sends a second reply with the same system bytes before SendTransaction has removed the entry
    /// (e.g. both replies arrive while the sending thread has not yet reached its await).
    /// </summary>
    [Fact]
    public async Task Second_reply_with_the_same_system_bytes_is_ignored()
    {
        var pending = new TaskCompletionSource<ILoggedSecsGemMessage>();
        transactions.ReplyExpectedMessages[SystemBytes] = ("S1F1", pending);
        var first = Reply();

        transactions.TryHandleDataMessageReply(first);
        var thrown = Record.Exception(() => transactions.TryHandleDataMessageReply(Reply()));

        Assert.Null(thrown);
        Assert.Same(first, await pending.Task);
        Assert.Contains(log.Messages, m => m.EndsWith("ignored: transaction already completed"));
    }

    /// <summary>
    /// RemoveAllTransactions (OnDisconnect) cancels the transaction between TryGetValue and completing it, while its
    /// reply is being handled.
    /// </summary>
    [Fact]
    public void Reply_for_a_cancelled_transaction_is_ignored()
    {
        var pending = new TaskCompletionSource<ILoggedSecsGemMessage>();
        transactions.ReplyExpectedMessages[SystemBytes] = ("S1F1", pending);
        pending.TrySetCanceled();

        var thrown = Record.Exception(() => transactions.TryHandleDataMessageReply(Reply()));

        Assert.Null(thrown);
        Assert.True(pending.Task.IsCanceled);
        Assert.Contains(log.Messages, m => m.EndsWith("ignored: transaction already completed"));
    }

    private ILoggedDataMessage Reply()
    {
        var message = new MessageFactory(1, 2, false, "On Line Data").Build();
        var (raw, header) = communication.BuildMessageData(message, SystemBytes);
        return new LoggedDataMessage(header, message, raw, MessageResult.Received) { Status = MessageStatus.Success };
    }

    private sealed class UnusedConnectionFactory : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) => new UnusedConnection();
    }

    private sealed class UnusedConnection : ITCPIPBase
    {
        public event EventHandler? OnConnect { add { } remove { } }
        public event EventHandler? OnDisconnected { add { } remove { } }
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public Task Connect() => Task.Delay(Timeout.Infinite);
        public Task<bool> SendData(ReadOnlyMemory<byte> data) => Task.FromResult(false);
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) OnDataReceived.Dispose();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (messages) return messages.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages) messages.Add(formatter(state, exception));
        }
    }
}
