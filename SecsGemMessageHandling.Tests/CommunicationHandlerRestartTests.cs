using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Enums;
using SecsGemMessageHandling.Helpers;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using TCPIPBaseLibrary.TCPBase;
using Xunit;

namespace SecsGemMessageHandling.Tests;

public class CommunicationHandlerRestartTests
{
    /// <summary>
    /// A local restart while the remote side is connected must leave the passive port open and the link must come
    /// back to Selected (GitLab secsgem/secsgembase#3). Before the fix the ObjectDisposedException of the replaced
    /// server connection made ConnectLoopAsync call ClosePort() on its successor. The race hits mostly on the first
    /// restart in a fresh process, so this test is meaningful mainly when it runs first.
    /// </summary>
    [Fact]
    public async Task Local_restart_while_connected_keeps_passive_port_open_and_reselects()
    {
        var port = GetFreePort();
        using var host = new HSMSStack(ConnectionMode.Passive, port, initiateSelect: false);
        using var equipment = new HSMSStack(ConnectionMode.Active, port, initiateSelect: true);

        host.Communication.OpenPort();
        equipment.Communication.OpenPort();
        await WaitUntilAsync(() => host.Selected && equipment.Selected, TimeSpan.FromSeconds(10),
            () => Describe(host, equipment));

        var hostSelects = host.SelectedCount;
        var equipmentSelects = equipment.SelectedCount;

        host.Communication.RestartConnection();

        // Both sides must pass through Selected again, i.e. the restart really happened and the link recovered.
        await WaitUntilAsync(() => host.SelectedCount > hostSelects && equipment.SelectedCount > equipmentSelects
                                   && host.Selected && equipment.Selected, TimeSpan.FromSeconds(5),
            () => Describe(host, equipment));
        // The replaced connection's loop ends asynchronously; give a late ClosePort() the chance to show up.
        await Task.Delay(500);
        Assert.True(host.Communication.ConnectionOn);
        Assert.Equal(ConnectionStatus.Connected, host.Communication.ConnectionStatus);
        Assert.True(host.Selected && equipment.Selected, Describe(host, equipment));
    }

    private static string Describe(HSMSStack host, HSMSStack equipment) =>
        $"host: ConnectionOn={host.Communication.ConnectionOn}, ConnectionStatus={host.Communication.ConnectionStatus}, " +
        $"HSMSStatus={host.Status}; equipment: ConnectionStatus={equipment.Communication.ConnectionStatus}, " +
        $"HSMSStatus={equipment.Status}";

    /// <summary>
    /// Host or equipment side: CommunicationHandler + TransactionHandler + ControlMessageHandling, wired like the DI
    /// registration but with a connection factory that creates a new TCP connection object each time.
    /// </summary>
    private sealed class HSMSStack : IDisposable
    {
        public CommunicationHandler Communication { get; }
        private ControlMessageHandling Control { get; }
        public HSMSStatus Status => Control.HSMSStatus;
        public bool Selected => Status == HSMSStatus.Selected;
        public int SelectedCount => Volatile.Read(ref selectedCount);
        private int selectedCount;

        public HSMSStack(ConnectionMode mode, int port, bool initiateSelect)
        {
            var eventBuses = new EventBusFactory(NullLoggerFactory.Instance);
            var settings = new NetworkSettings
            {
                IpAddress = IPAddress.Loopback.ToString(),
                Port = (ushort)port,
                ConnectionMode = mode
            };
            var parameters = new HSMSParameters { T5 = 1000, InitiateSelectRequest = initiateSelect };
            var controlMessages = new ControlMessageFactory(parameters);

            Communication = new CommunicationHandler(settings, new ConnectionFactory(settings, eventBuses),
                NullLogger<CommunicationHandler>.Instance, parameters, controlMessages, eventBuses);
            var transactions = new TransactionHandler(parameters, NullLogger<TransactionHandler>.Instance,
                Communication, eventBuses);
            Control = new ControlMessageHandling(Communication, NullLogger<ControlMessageHandling>.Instance,
                parameters, controlMessages, transactions);
            Control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ControlMessageHandling.HSMSStatus) && Selected)
                    Interlocked.Increment(ref selectedCount);
            };
        }

        public void Dispose()
        {
            Communication.ClosePort();
            Communication.Dispose();
        }
    }

    private sealed class ConnectionFactory(INetworkSettings settings, EventBusFactory eventBuses)
        : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) => connectionMode switch
        {
            ConnectionMode.Active => new TCPIPClientBase(settings, NullLogger<TCPIPClientBase>.Instance, eventBuses),
            ConnectionMode.Passive => new TCPIPServerBase(settings, NullLogger<TCPIPServerBase>.Instance, eventBuses),
            _ => throw new ArgumentOutOfRangeException(nameof(connectionMode), connectionMode, null)
        };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> state)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition was not met within {timeout} ({state()})");
            await Task.Delay(10);
        }
    }
}
