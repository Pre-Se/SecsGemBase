using System.Net;
using Logging;
using Logging.Enums;
using Logging.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Header;
using SecsGemBaseItems.Enums;
using SecsGemBaseItems.SecsGemParameters;
using SecsGemHelperClasses;
using SecsGemHelperClasses.Interfaces;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Events;
using SecsGemMessageHandling.Events.Builders;
using SecsGemMessageHandling.Events.Enums;
using SecsGemMessageHandling.Events.Models;
using SecsGemMessageHandling.Events.Registry;
using SecsGemMessageHandling.Helpers;
using TCPIPBaseLibrary.Interfaces;
using TCPIPBaseLibrary.Network;
using Xunit;

namespace SecsGemMessageHandling.Tests;

/// <summary>
/// S1F3 (Selected Equipment Status Request) answered by SecsGemEventReportHandler: S1F4 has one entry per requested
/// SVID, in the order of the request (GitLab secsgem/secsgembase#43). Until then the answer stopped at the first SVID
/// that was not I4, so the host could not match the values to its SVIDs.
/// </summary>
public class S1F3HandlerTests
{
    private static readonly EventBusFactory EventBuses = new(NullLoggerFactory.Instance);

    [Fact]
    public async Task Each_requested_SVID_keeps_its_position_in_S1F4()
    {
        using var equipment = new Equipment();
        equipment.AddStatusVariable(1001, "running");
        equipment.AddStatusVariable(1002, "42");

        var reply = await equipment.RequestAsync(list => list
            .AddU4(1001)           // wrong format (I4 expected)
            .AddI4(1002)
            .AddAscii("1001")      // wrong format
            .AddI4(999)            // unknown
            .AddI4(1001));

        Assert.Collection(Entries(reply),
            entry => AssertEmptyList(entry),
            entry => Assert.Equal(["42"], entry.GetStringValues()),
            entry => AssertEmptyList(entry),
            entry => AssertEmptyList(entry),
            entry => Assert.Equal(["running"], entry.GetStringValues()));
    }

    [Fact]
    public async Task Only_invalid_SVIDs_give_one_empty_list_each()
    {
        using var equipment = new Equipment();
        equipment.AddStatusVariable(1, "on");

        var reply = await equipment.RequestAsync(list => list.AddU4(1).AddU2(2));

        Assert.Collection(Entries(reply), AssertEmptyList, AssertEmptyList);
    }

    /// <summary>
    /// The S1F4 list takes its entries as children. With the registry's own item, the second request of SVID 1 moved
    /// that item to the end: two entries instead of three, and the host read the value of SVID 2 for SVID 1.
    /// </summary>
    [Fact]
    public async Task SVID_requested_twice_appears_twice_at_its_positions()
    {
        using var equipment = new Equipment();
        equipment.AddStatusVariable(1, "a");
        equipment.AddStatusVariable(2, "b");

        var reply = await equipment.RequestAsync(list => list.AddI4(1).AddI4(2).AddI4(1));

        Assert.Collection(Entries(reply),
            entry => Assert.Equal(["a"], entry.GetStringValues()),
            entry => Assert.Equal(["b"], entry.GetStringValues()),
            entry => Assert.Equal(["a"], entry.GetStringValues()));
    }

    /// <summary>The registry keeps its items: answering does not take them over into the message.</summary>
    [Fact]
    public async Task Answering_leaves_the_registry_items_alone()
    {
        using var equipment = new Equipment();
        var variable = equipment.AddStatusVariable(1, "a");

        await equipment.RequestAsync(list => list.AddI4(1));

        Assert.Null(variable.Item.Parent);
    }

    /// <summary>An I4 with several values is no SVID: it is answered with an empty list, not with its first value.</summary>
    [Fact]
    public async Task I4_with_several_values_is_answered_with_an_empty_list()
    {
        using var equipment = new Equipment();
        equipment.AddStatusVariable(1, "a");

        var reply = await equipment.RequestAsync(list => list
            .AddItem(new SecsGemValueItem<int> { FormatType = SecsGemItemFormatType.I4, Values = [1, 2] }));

        Assert.Collection(Entries(reply), AssertEmptyList);
    }

    [Fact]
    public async Task Valid_SVIDs_are_answered_in_the_order_of_the_request()
    {
        using var equipment = new Equipment();
        equipment.AddStatusVariable(1, "a");
        equipment.AddStatusVariable(2, "b");

        var reply = await equipment.RequestAsync(list => list.AddI4(2).AddI4(1));

        Assert.Collection(Entries(reply),
            entry => Assert.Equal(["b"], entry.GetStringValues()),
            entry => Assert.Equal(["a"], entry.GetStringValues()));
    }

    private static List<SecsGemItem> Entries(ILoggedDataMessage reply)
    {
        var body = Assert.IsType<SecsGemItem>(Assert.Single(reply.Data.Children), exactMatch: false);
        Assert.Equal(SecsGemItemFormatType.List, body.FormatType);
        return body.Children.Cast<SecsGemItem>().ToList();
    }

    private static void AssertEmptyList(SecsGemItem entry)
    {
        Assert.Equal(SecsGemItemFormatType.List, entry.FormatType);
        Assert.Empty(entry.Children);
    }

    /// <summary>
    /// The equipment side wired like the DI registration, Online/Local, with a connection object that accepts every
    /// write. IgnoreState lets S1F4 out without an HSMS session.
    /// </summary>
    private sealed class Equipment : IDisposable
    {
        private const uint RequestSystemBytes = 0x1234;
        private readonly CommunicationHandler communication;
        private readonly DataMessageHandler data;
        private readonly SecsGemRegistry<SecsGemEquipmentVariable> variables;
        private readonly TaskCompletionSource<ILoggedDataMessage> reply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Equipment()
        {
            var settings = new NetworkSettings
            {
                IpAddress = IPAddress.Loopback.ToString(), Port = 5000, ConnectionMode = ConnectionMode.Passive
            };
            var parameters = new HSMSParameters { IgnoreState = true };
            var controlMessages = new ControlMessageFactory(parameters);
            communication = new CommunicationHandler(settings, new AcceptingConnectionFactory(),
                NullLogger<CommunicationHandler>.Instance, parameters, controlMessages, EventBuses);
            var transactions = new TransactionHandler(parameters, NullLogger<TransactionHandler>.Instance,
                communication, EventBuses);
            var control = new ControlMessageHandling(communication, NullLogger<ControlMessageHandling>.Instance,
                parameters, controlMessages, transactions);
            var controlState = new ControlStateInfo
            {
                ControlState = ControlState.Online, ControlSubstate = ControlSubstate.OnlineLocal
            };
            data = new DataMessageHandler(parameters, communication, NullLogger<DataMessageHandler>.Instance,
                transactions, controlState, control, EventBuses);
            var controlStateHandler = new ControlStateHandler(data, NullLogger<ControlStateHandler>.Instance,
                controlState, EventBuses);

            var events = new SecsGemRegistry<SecsGemEventReport>(
                NullLogger<SecsGemRegistry<SecsGemEventReport>>.Instance, EventBuses);
            var reports = new SecsGemRegistry<SecsGemReport>(
                NullLogger<SecsGemRegistry<SecsGemReport>>.Instance, EventBuses);
            variables = new SecsGemRegistry<SecsGemEquipmentVariable>(
                NullLogger<SecsGemRegistry<SecsGemEquipmentVariable>>.Instance, EventBuses);
            _ = new SecsGemEventReportHandler(
                new LinkEventReportRegistry(events, reports, NullLogger<LinkEventReportRegistry>.Instance),
                reports, variables, controlStateHandler, data, events,
                NullLogger<SecsGemEventReportHandler>.Instance,
                new SecsGemEventBuilder(events,
                    new SecsGemReportBuilder(reports, variables, NullLogger<SecsGemReportBuilder>.Instance),
                    NullLogger<SecsGemEventBuilder>.Instance));

            communication.OnDataMessageOut.Subscribe(sent =>
            {
                if (sent.Data is { Stream: 1, Function: 4 } && sent.HeaderData.SystemBytes == RequestSystemBytes)
                    reply.TrySetResult(sent);
            });
        }

        public SecsGemEquipmentVariable AddStatusVariable(int svid, string value)
        {
            var variable = new SecsGemEquipmentVariable
            {
                VariableId = svid, VariableClass = SecsGemVariableClass.StatusVariable, Value = value
            };
            Assert.True(variables.Add(variable));
            Assert.True(variables.TryGet(svid, out var registered));
            return registered;
        }

        /// <summary>Delivers S1F3 with this SVID list as if received, returns the S1F4 sent in reply.</summary>
        public async Task<ILoggedDataMessage> RequestAsync(Action<ItemFactory> svids)
        {
            var request = new MessageFactory(1, 3, true, "Selected Equipment Status Request",
                new ItemFactory().AddList(svids)).Build();
            var header = new HeaderData { SystemBytes = RequestSystemBytes };
            data.OnMessageInCommunicationsEstablished.Publish(
                new LoggedDataMessage(header, request, [], MessageResult.Received));
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose() => communication.Dispose();
    }

    private sealed class AcceptingConnectionFactory : INetworkConnectionFactory
    {
        public ITCPIPBase CreateTCPIPConnection(ConnectionMode connectionMode) => new AcceptingConnection();
    }

    private sealed class AcceptingConnection : ITCPIPBase
    {
        public event EventHandler? OnConnect { add { } remove { } }
        public event EventHandler? OnDisconnected { add { } remove { } }
        public IEventBus<ReadOnlyMemory<byte>> OnDataReceived { get; } = EventBuses.CreateTransient<ReadOnlyMemory<byte>>();
        public Task Connect() => Task.Delay(Timeout.Infinite);
        public Task<bool> SendData(ReadOnlyMemory<byte> data) => Task.FromResult(true);
        public void Dispose() { }
    }
}
