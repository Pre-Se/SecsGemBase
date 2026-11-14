using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemBaseItems.Data_Containers.Serialization;
using SecsGemBaseItems.LibraryManager;
using SecsGemBaseItems.Responders;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Enums;
using SecsGemMessageHandling.Helpers;
using SecsGemScenarioEngine.Models;
using SecsGemScenarioEngine.Services;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;

namespace SecsGemScenarioEngine.Tests;

/// <summary>
/// One side of a loopback HSMS link, wired like the DI registration in EvenBetterFastSim, with real TCP objects:
/// CommunicationHandler, TransactionHandler, ControlMessageHandling, DataMessageHandler and a ScenarioExecutionService.
/// </summary>
internal sealed class Side : IDisposable
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    private readonly HSMSParameters parameters;
    private int connects;
    private long lastConnectTicks;
    private long lastDisconnectTicks;

    public Side(ConnectionMode mode, int port, bool withHandlersForEngine = true)
    {
        var settings = new NetworkSettings
        {
            IpAddress = IPAddress.Loopback.ToString(),
            Port = (ushort)port,
            ConnectionMode = mode
        };
        // The active side selects. Short T5 and S1F13 delay keep the tests fast.
        parameters = new HSMSParameters
        {
            T5 = 200,
            InitiateSelectRequest = mode == ConnectionMode.Active,
            CommunicationsWaitDelay = 100
        };
        ControlMessages = new ControlMessageFactory(parameters);
        Communication = new CommunicationHandler(settings, new ConnectionFactory(settings, TcpLog),
            NullLogger<CommunicationHandler>.Instance, parameters, ControlMessages, EventBuses);
        Communication.OnConnect += (_, _) =>
        {
            Interlocked.Exchange(ref lastConnectTicks, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref connects);
        };
        Communication.OnDisconnect += (_, _) => Interlocked.Exchange(ref lastDisconnectTicks, Stopwatch.GetTimestamp());
        var transactions = new TransactionHandler(parameters, NullLogger<TransactionHandler>.Instance, Communication,
            EventBuses);
        Control = new ControlMessageHandling(Communication, NullLogger<ControlMessageHandling>.Instance, parameters,
            ControlMessages, transactions);
        Data = new DataMessageHandler(parameters, Communication, NullLogger<DataMessageHandler>.Instance, transactions,
            new ControlStateInfo(), Control, EventBuses);
        Engine = withHandlersForEngine
            ? new ScenarioExecutionService(Data, new EmptyLibrary(), EngineLog.For<ScenarioExecutionService>(),
                new ScenarioReplyGuard(), Communication, Control)
            : new ScenarioExecutionService(Data, new EmptyLibrary(), EngineLog.For<ScenarioExecutionService>(),
                new ScenarioReplyGuard());
    }

    public CommunicationHandler Communication { get; }
    public ControlMessageHandling Control { get; }
    public DataMessageHandler Data { get; }
    public ControlMessageFactory ControlMessages { get; }
    public ScenarioExecutionService Engine { get; }

    /// <summary>What this side's TCP objects logged.</summary>
    public Log TcpLog { get; } = new();

    /// <summary>What the scenario engine logged.</summary>
    public Log EngineLog { get; } = new();

    /// <summary>Data messages can be exchanged: HSMS Selected and S1F13/S1F14 done.</summary>
    public bool Ready => Control.HSMSStatus == HSMSStatus.Selected && Data.CommunicationsEstablished;

    public int Connects => Volatile.Read(ref connects);

    /// <summary>Time from the last OnDisconnect to the last OnConnect after it.</summary>
    public TimeSpan LastOutage =>
        Stopwatch.GetElapsedTime(Interlocked.Read(ref lastDisconnectTicks), Interlocked.Read(ref lastConnectTicks));

    public string Describe() =>
        $"{Communication.ConnectionStatus}/{Control.HSMSStatus}, established={Data.CommunicationsEstablished}, " +
        $"connects={Connects}";

    /// <summary>Sends S6F11 with this CEID, without the checks of DataMessageHandler (control state).</summary>
    public async Task SendEventAsync(uint ceid)
    {
        var sent = await Communication.SendAndLogMessage(Scenario.EventReport(ceid), Data.NewSystemBytes());
        if (sent.Status != Logging.Enums.MessageStatus.Success)
            throw new InvalidOperationException($"S6F11 CEID {ceid} not sent ({Describe()})");
    }

    public void Dispose()
    {
        Communication.ClosePort();
        Communication.Dispose();
    }

    private sealed class ConnectionFactory(INetworkSettings settings, Log log) : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) => connectionMode switch
        {
            ConnectionMode.Active => new TCPIPClientBase(settings, log.For<TCPIPClientBase>(), EventBuses),
            ConnectionMode.Passive => new TCPIPServerBase(settings, log.For<TCPIPServerBase>(), EventBuses),
            _ => throw new ArgumentOutOfRangeException(nameof(connectionMode), connectionMode, null)
        };
    }

    private sealed class EmptyLibrary : ISecsGemLibraryManager
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public ObservableCollection<SecsGemTransaction> Library { get; } = [];
        public IDataItem? SelectedItem { get; set; }
        public void OpenLibrary() => throw new NotSupportedException();
        public void AddItemToLibrary(IDataItem item) => throw new NotSupportedException();
    }
}

/// <summary>Two sides connected over loopback; the simulator (whose scenarios run) is either side.</summary>
internal sealed class Link : IDisposable
{
    private Link(Side simulator, Side remote)
    {
        Simulator = simulator;
        Remote = remote;
    }

    public Side Simulator { get; }
    public Side Remote { get; }

    /// <summary>Both sides connected and ready to exchange data messages.</summary>
    public static async Task<Link> ConnectAsync(ConnectionMode simulatorMode)
    {
        var port = Wait.GetFreePort();
        var remoteMode = simulatorMode == ConnectionMode.Active ? ConnectionMode.Passive : ConnectionMode.Active;
        var link = new Link(new Side(simulatorMode, port), new Side(remoteMode, port));
        link.Simulator.Communication.OpenPort();
        link.Remote.Communication.OpenPort();
        await link.UntilReadyAsync(connects: 1);
        return link;
    }

    /// <summary>Waits until both sides are ready, each on its <paramref name="connects"/>th connection.</summary>
    public Task UntilReadyAsync(int connects) =>
        Wait.UntilAsync(() => Simulator.Ready && Remote.Ready && Simulator.Connects == connects &&
                              Remote.Connects == connects, Describe);

    public string Describe() => $"simulator: {Simulator.Describe()}; remote: {Remote.Describe()}";

    public void Dispose()
    {
        Simulator.Dispose();
        Remote.Dispose();
    }
}

/// <summary>Builds scenarios.</summary>
internal static class Scenario
{
    public static SecsGemDataMessage EventReport(uint ceid) =>
        new MessageFactory(6, 11, false, "Event Report Send", new ItemFactory()
            .AddList(body => body
                .AddU4(1, "DATAID")
                .AddU4(ceid, "CEID")
                .AddList(_ => { }))).Build();

    /// <summary>Receive node for S6F11 with this CEID.</summary>
    public static ScenarioNode Receive(uint ceid) => new()
    {
        Type = NodeType.Receive,
        TransactionJson = SecsGemTransactionJsonConverter.Serialize(new SecsGemTransaction
        {
            PrimaryMessage = EventReport(ceid),
            ReplyMessage = new MessageFactory(6, 12, false, "Event Report Acknowledge").Build()
        }),
        MatchConditionsJson = ResponderJson.SerializeConditions(
            [new MatchCondition { ItemPath = "0.1", Value = ceid.ToString() }])
    };

    public static ScenarioNode Node(NodeType type, string? parameter) =>
        new() { Type = type, TransactionName = parameter };

    /// <summary>Start → each chain in parallel (its nodes one after another) → End.</summary>
    public static ScenarioGraph Of(params ScenarioNode[][] chains)
    {
        var start = new ScenarioNode { Type = NodeType.Start };
        var graph = new ScenarioGraph { Name = "test", Nodes = [start] };
        foreach (var chain in chains)
        {
            var end = new ScenarioNode { Type = NodeType.End };
            graph.Nodes.AddRange(chain);
            graph.Nodes.Add(end);
            var previous = start;
            foreach (var node in chain.Append(end))
            {
                graph.Edges.Add(new ScenarioEdge { SourceNodeId = previous.Id, TargetNodeId = node.Id });
                previous = node;
            }
        }

        return graph;
    }

    public static ScenarioGraph Of(params ScenarioNode[] chain) => Of([chain]);
}

/// <summary>Records node states as they are reported (synchronously, unlike Progress&lt;T&gt;).</summary>
internal sealed class ProgressLog(Action<ScenarioNodeProgress>? onReport = null) : IProgress<ScenarioNodeProgress>
{
    private readonly List<ScenarioNodeProgress> reports = [];

    public void Report(ScenarioNodeProgress value)
    {
        onReport?.Invoke(value);
        lock (reports) reports.Add(value);
    }

    public bool Reached(ScenarioNode node, ScenarioNodeRunState state)
    {
        lock (reports) return reports.Any(r => r.NodeId == node.Id && r.State == state);
    }
}

/// <summary>Collects log messages.</summary>
internal sealed class Log
{
    private readonly List<string> messages = [];

    public IReadOnlyList<string> Messages
    {
        get { lock (messages) return messages.ToList(); }
    }

    public ILogger<T> For<T>() => new Logger<T>(this);

    private sealed class Logger<T>(Log log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (log.messages) log.messages.Add(formatter(state, exception));
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, Func<string> state, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition was not met in time ({state()})");
            await Task.Delay(10);
        }
    }

    public static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
