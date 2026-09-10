using FluentResults;
using Logging.Interfaces;
using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemMessageHandling.Data_Handling;
using SecsGemMessageHandling.Events.Builders;
using SecsGemMessageHandling.Events.Enums;
using SecsGemMessageHandling.Events.Models;
using SecsGemMessageHandling.Events.Registry;
using SecsGemMessageHandling.Events.Registry.Interface;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using SecsGemBaseItems.Enums;

namespace SecsGemMessageHandling.Events;

/// <summary>
/// Class responsible for parsing and replying to messages related to Event Report Linking (S2F33, S2F35 and S2F37)
/// </summary>
public class SecsGemEventReportHandler
{
    private readonly LinkEventReportRegistry linkEventReportRegistry;
    private readonly IRegistry<SecsGemEventReport> eventRegistry;
    private readonly IRegistry<SecsGemReport> reportRegistry;
    private readonly IRegistry<SecsGemEquipmentVariable> variableRegistry;
    private readonly DataMessageHandler dataMessageHandler;
    private readonly SecsGemEventBuilder eventBuilder;
    private readonly ILogger<SecsGemEventReportHandler> logger;

    public SecsGemEventReportHandler(LinkEventReportRegistry linkEventReportRegistry,
        IRegistry<SecsGemReport> reportRegistry,
        IRegistry<SecsGemEquipmentVariable> variableRegistry,
        ControlStateHandler controlStateHandler,
        DataMessageHandler dataMessageHandler,
        IRegistry<SecsGemEventReport> eventRegistry,
        ILogger<SecsGemEventReportHandler> logger,
        SecsGemEventBuilder eventBuilder)
    {
        this.linkEventReportRegistry = linkEventReportRegistry;
        this.reportRegistry = reportRegistry;
        this.variableRegistry = variableRegistry;
        this.dataMessageHandler = dataMessageHandler;
        this.logger = logger;
        this.eventBuilder = eventBuilder;
        this.eventRegistry = eventRegistry;

        controlStateHandler.OnMessageInOnlineLocal.Subscribe(HandleReceivedMessage);
        controlStateHandler.OnMessageInOnlineRemote.Subscribe(HandleReceivedMessage);
    }

    private async Task HandleReceivedMessage(ILoggedControlStateDataMessage loggedMessage)
    {
        var message = loggedMessage.Message.Data;
        var stream = message.Stream;
        var function = message.Function;
        var replyBytes = loggedMessage.Message.HeaderData.SystemBytes;

        switch (stream)
        {
            case 1:
                switch (function)
                {
                    case 3:
                        await HandleS1F03Message(message, replyBytes);
                        break;
                }
                break;
            case 2:
                switch (function)
                {
                    case 33:
                        await HandleS2F33Message(message, replyBytes);
                        break;
                    case 35:
                        await HandleS2F35Message(message, replyBytes);
                        break;
                    case 37:
                        await HandleS2F37Message(message, replyBytes);
                        break;
                }
                break;
        }
    }

    private async Task<bool> HandleS1F03Message(SecsGemDataMessage message, uint replyBytes)
    {
        var variableList = message[0];

        if (variableList is not {FormatType: SecsGemItemFormatType.List})
        {
            logger.LogError("S1F03 Message received with illegal format: missing variable list");
            return false;
        }

        List<SecsGemItem> statusVariables;

        if (variableList is { Children.Count: 0 })
        {
            statusVariables = GetAllStatusVariables();
        }
        else
        {
            if (!GetRequestedStatusVariables(variableList, out statusVariables));
        }

        await SendS1F04Message(statusVariables, replyBytes);
        return true;
    }

    private bool GetRequestedStatusVariables(SecsGemItem variableList, out List<SecsGemItem> statusVariables)
    {
        statusVariables = [];

        foreach (var statusVariableId in variableList.Children.Cast<SecsGemItem>())
        {
            if (statusVariableId is not { FormatType: SecsGemItemFormatType.I4 })
            {
                logger.LogError("Invalid status variable id format, format should be I4");
                return false;
            }

            if (!statusVariableId.TryGetValue<int>(out var svid))
            {
                logger.LogError("Error parsing status variable id");
                return false;
            }

            statusVariables.Add(GetStatusVariable(svid));
        }

        return true;
    }

    private async Task SendS1F04Message(IEnumerable<SecsGemItem> statusVariablesValues, uint replyBytes)
    {
        var selectedEquipmentStatusData =
            new MessageFactory(1, 4, false, "Selected Equipment Status Data", new ItemFactory()
                    .AddList(statusVariables =>
                    {
                        foreach (var statusVariable in statusVariablesValues)
                        {
                            statusVariables.AddItem(statusVariable);
                        }
                    }))
                .Build();

        await dataMessageHandler.SendDataMessage(selectedEquipmentStatusData, replyBytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a Define Report (S2F33) message, adds or removes reports according to the message data
    /// </summary>
    /// <param name="message"></param>
    /// <param name="replyBytes"></param>
    /// <returns></returns>
    private async Task<bool> HandleS2F33Message(SecsGemDataMessage message, uint replyBytes)
    {
        var reportList = message[0]?[1];

        if (reportList is not {FormatType:SecsGemItemFormatType.List})
        {
            logger.LogError("S2F33 Message received without a report list");
            return false;
        }

        if (reportList is { Children.Count: 0 })
        {
            linkEventReportRegistry.DeleteAllReports();
            await SendS2F34Message(DRACK.Accepted, replyBytes);
            return true;
        }

        var reportIds = new Dictionary<int, List<int>>();

        foreach (var reportGroup in reportList.Children.Cast<SecsGemItem>())
        {
            var reportId = reportGroup[0];

            if (reportId is not {FormatType: SecsGemItemFormatType.I4})
            {
                logger.LogError("Message S2F33 with invalid format");
                return false;
            }

            if (!reportId.TryGetValue<int>(out var intRptid))
            {
                logger.LogError("Report ID [{report}] could not be parsed into a integer", reportId.GetBoxedValues().FirstOrDefault());
                await SendS2F34Message(DRACK.InvalidFormat, replyBytes);
                return false;
            }

            if (reportIds.ContainsKey(intRptid))
            {
                logger.LogError("Report ID [{rptid}] was already defined in this message", intRptid);
                await SendS2F34Message(DRACK.RptidAlreadyDefined, replyBytes);
                return false;
            }

            var reportVariablesList = reportGroup[1];

            if (reportVariablesList == null)
            {
                logger.LogError("S2F33 message includes report [{rptid}] but no variable id list included", intRptid);
                return false;
            }

            var result = GetVariablesForReport(reportVariablesList.Children);

            if (result.IsFailed)
            {
                await SendS2F34Message(DRACK.VidNotDefined, replyBytes);
                logger.LogError("{error}", result.ToString());
                return false;
            }

            var variableList = result.Value;
            reportIds.TryAdd(intRptid, variableList);
        }

        foreach (var kvp in reportIds)
        {
            var rptid = kvp.Key;
            var variableIds = kvp.Value;

            if (variableIds.Count == 0)
            {
                linkEventReportRegistry.DeleteReport(kvp.Key);
            }

            var report = new SecsGemReport()
            {
                Rptid = rptid,
                Variables = variableIds.ToImmutableList()
            };

            reportRegistry.Add(report);
        }

        await SendS2F34Message(DRACK.Accepted, replyBytes);
        return true;
    }

    private async Task SendS2F34Message(DRACK responseCode, uint replyBytes)
    {
        var defineReportAcknowledge = new MessageFactory(2, 34, false, "Define Report Acknowledge (DRA)", 
                new ItemFactory()
                    .AddBinary((byte)responseCode))
            .Build();

        await dataMessageHandler.SendDataMessage(defineReportAcknowledge, replyBytes).ConfigureAwait(false);
    }

    private async Task<bool> HandleS2F35Message(SecsGemDataMessage message, uint replyBytes)
    {
        var linkEventReportList = message[0]?[1];

        if (linkEventReportList is not { FormatType: SecsGemItemFormatType.List })
        {
            logger.LogError("S2F35 (Link Event Report) Message received without a link event report list");
            return false;
        }

        var linkEventReportsPair = new Dictionary<int, List<int>>();

        foreach (var linkEvent in linkEventReportList.Children.Cast<SecsGemItem>())
        {
            var eventCeid = linkEvent[0];

            if (eventCeid == null)
            {
                logger.LogError("Message S2F35 with invalid format");
                return false;
            }

            if (!eventCeid.TryGetValue<int>(out var intCeid))
            {
                logger.LogError("CEID [{ceid}] could not be parsed into a integer", eventCeid.GetBoxedValues().FirstOrDefault());
                await SendS2F36Message(LRACK.InvalidFormat, replyBytes);
                return false;
            }

            if (!eventRegistry.Contains(intCeid))
            {
                logger.LogError("CEID [{ceid}] is not defined", intCeid);
                await SendS2F36Message(LRACK.CeidNotDefined, replyBytes);
                return false;
            }

            if (linkEventReportsPair.ContainsKey(intCeid))
            {
                logger.LogError("CEID [{ceid}] was already defined in this message", intCeid);
                await SendS2F36Message(LRACK.CeidAlreadyDefined, replyBytes);
                return false;
            }

            var reportVariablesList = linkEvent[1];

            if (reportVariablesList == null)
            {
                logger.LogError("S2F35 message includes ceid [{ceid}] but no report id list included", intCeid);
                await SendS2F36Message(LRACK.InvalidFormat, replyBytes);
                return false;
            }

            var result = GetReportsForEvent(reportVariablesList.Children);

            if (result.IsFailed)
            {
                await SendS2F36Message(LRACK.RptidNotDefined, replyBytes);
                logger.LogError("{error}", result.ToString());
                return false;
            }

            linkEventReportsPair.TryAdd(intCeid, result.Value);
        }

        foreach (var kvp in linkEventReportsPair)
        {
            var ceid = kvp.Key;
            var reports = kvp.Value;

            linkEventReportRegistry.AddReportsToEvent(ceid, reports);
        }

        await SendS2F36Message(LRACK.Accepted, replyBytes);
        return true;
    }

    private async Task SendS2F36Message(LRACK responseCode, uint replyBytes)
    {
        var defineReportAcknowledge = new MessageFactory(2, 36, false, "Link Event Report Acknowledge (LERA)",
                new ItemFactory()
                    .AddBinary((byte)responseCode))
            .Build();

        await dataMessageHandler.SendDataMessage(defineReportAcknowledge, replyBytes).ConfigureAwait(false);
    }

    private async Task<bool> HandleS2F37Message(SecsGemDataMessage message, uint replyBytes)
    {
        var ceedItem = message[0]?[0];
        if (ceedItem is not { FormatType: SecsGemItemFormatType.Boolean })
        {
            logger.LogError("S2F37 (Enable/Disable Event Report) Message received without a Collection event enable/disable code");
            return false;
        }

        if (!ceedItem.TryGetValue<bool>(out var ceedBoolean))
        {
            logger.LogError("S2F37 (Enable/Disable Event Report) Message received with invalid format for Collection event enable/disable code, item must be a boolean");
            return false;
        }

        var ceidList = message[0]?[1];

        if (ceidList is not { FormatType: SecsGemItemFormatType.List })
        {
            logger.LogError("S2F37 (Enable/Disable Event Report) Message received without a ceid list");
            return false;
        }

        var ceidSet = new HashSet<int>();

        if (ceidList.Children.Count == 0)
        {
            foreach (var eventReport in eventRegistry.GetAll())
            {
                ceidSet.Add(eventReport.Ceid);
            }
        }
        else
        {
            foreach (var eventCeid in ceidList.Children.Cast<SecsGemItem>())
            {
                if (eventCeid == null)
                {
                    logger.LogError("Message S2F37 with invalid format");
                    return false;
                }

                if (!eventCeid.TryGetValue<int>(out var intCeid))
                {
                    logger.LogError("CEID [{ceid}] could not be parsed into a integer", eventCeid.GetBoxedValues().FirstOrDefault());
                    return false;
                }

                if (!eventRegistry.Contains(intCeid))
                {
                    logger.LogError("CEID [{ceid}] is not defined", intCeid);
                    await SendS2F38Message(ERACK.CeidNotDefined, replyBytes);
                    return false;
                }

                ceidSet.Add(intCeid);
            }
        }


        foreach (var ceid in ceidSet)
        {
            SetEventActive(ceid, ceedBoolean);
        }

        await SendS2F38Message(ERACK.Accepted, replyBytes);
        return true;
    }

    private async Task SendS2F38Message(ERACK responseCode, uint replyBytes)
    {
        var defineReportAcknowledge = new MessageFactory(2, 38, false, "Enable / Disable Event Report Acknowledge (EERA)",
                new ItemFactory()
                    .AddBinary((byte)responseCode))
            .Build();

        await dataMessageHandler.SendDataMessage(defineReportAcknowledge, replyBytes).ConfigureAwait(false);
    }

    public async Task SendS6F11Message(int ceid)
    {
        if (eventBuilder.TryBuildEventReportMessage(ceid, out var message))
        {
            await dataMessageHandler.SendDataMessage(message).ConfigureAwait(false);
        }
        else
        {
            logger.LogWarning("Event Message {ceid} could not be built, event will not be sent", ceid);
        }
    }

    private Result<List<int>> GetVariablesForReport(IEnumerable<IDataItem> variableItem)
    {
        var variableList = new List<int>();

        foreach (var variableId in variableItem.Cast<SecsGemItem>())
        {
            var vid = variableId.GetStringValues().FirstOrDefault() ?? "";
            if (!variableId.TryGetValue<int>(out var intVid))
            {
                return Result.Fail<List<int>>(new Error($"Variable ID [{vid}] could not be parsed into a integer"));
            }

            if (!variableRegistry.Contains(intVid))
            {
                return Result.Fail<List<int>>($"Variable ID [{intVid}] is not defined");
            }
            variableList.Add(intVid);
        }

        return Result.Ok(variableList);
    }

    private Result<List<int>> GetReportsForEvent(ObservableCollection<IDataItem> variableItem)
    {
        var reportList = new List<int>();

        foreach (var reportItem in variableItem.Cast<SecsGemItem>())
        {
            var rptid = reportItem.GetStringValues().FirstOrDefault() ?? "";
            if (!reportItem.TryGetValue<int>(out var intRptid))
            {
                return Result.Fail<List<int>>(new Error($"Report ID [{rptid}] could not be parsed into a integer"));
            }

            if (!reportRegistry.Contains(intRptid))
            {
                return Result.Fail<List<int>>($"Report ID [{intRptid}] is not defined");
            }
            reportList.Add(intRptid);
        }

        return Result.Ok(reportList);
    }

    public void SetEventActive(int ceid, bool active)
    {
        if (!eventRegistry.TryGet(ceid, out var oldEvent))
        {
            logger.LogError("Event with CEID [{ceid}] doesn't exist, cannot be set active", ceid);
            return;
        }

        var newEvent = oldEvent with { IsActive = active };

        eventRegistry.Update(newEvent, oldEvent);
    }

    private List<SecsGemItem> GetAllStatusVariables()
    {
        var statusVariables = new List<SecsGemItem>();

        foreach (var variable in variableRegistry.GetAll())
        {
            if (variable.VariableClass is SecsGemVariableClass.StatusVariable)
            {
                statusVariables.Add(variable.Item);
            }
        }

        return statusVariables;
    }

    private SecsGemItem GetStatusVariable(int statusVariableId)
    {
        if (variableRegistry.TryGet(statusVariableId, out var variable) &&
            variable is { VariableClass: SecsGemVariableClass.StatusVariable })
        {
            return variable.Item;
        }
        else
        {
            var missingVar = SecsGemItem.Create(SecsGemItemFormatType.List);
            missingVar.Description = $"Status Variable [{statusVariableId}] not found";
            return missingVar;
        }
    }
}
