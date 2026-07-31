using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SecsGemMessageHandling.Events.Models;
using SecsGemMessageHandling.Events.Registry.Interface;

namespace SecsGemMessageHandling.Events.Registry;

/// <summary>
/// Class responsible for handling relations between Events and Reports
/// </summary>
/// <param name="eventRegistry"></param>
/// <param name="reportRegistry"></param>
/// <param name="logger"></param>
public class LinkEventReportRegistry(IRegistry<SecsGemEventReport> eventRegistry, IRegistry<SecsGemReport> reportRegistry, ILogger<LinkEventReportRegistry> logger)
{
    private readonly IRegistry<SecsGemEventReport> eventRegistry = eventRegistry;
    private readonly IRegistry<SecsGemReport> reportRegistry = reportRegistry;

    public bool AddReportsToEvent(int ceid, List<int> reportsIds)
    {
        if (!eventRegistry.TryGet(ceid, out var oldEvent))
        {
            logger.LogError("Event with CEID [{ceid}] could not be found while trying to add a report to it", ceid);
            return false;
        }

        foreach(var rptid in reportsIds)
        {
            if (reportRegistry.TryGet(rptid, out var report)) continue;

            logger.LogError("Report with rptid [{rptid}] could not be found while trying to add it to event", rptid);
            return false;
        }

        var newEvent = oldEvent with
        {
            ReportList = reportsIds.ToImmutableList()
        };

        eventRegistry.Update(newEvent, oldEvent);

        return true;
    }

    public bool DeleteReport(int rptid)
    {
        RemoveReportReferencesFromEvents(rptid);
        return reportRegistry.Delete(rptid);
    }

    public void DeleteAllReports()
    {
        foreach (var report in reportRegistry.GetAll())
        {
            DeleteReport(report.Id);
        }
    }

    private void RemoveReportReferencesFromEvents(int rptid)
    {
        foreach (var oldEvent in eventRegistry.GetAll())
        {
            if (!oldEvent.ReportList.Contains(rptid)) continue;

            var newList = oldEvent.ReportList
                .Where(id => id != rptid)
                .ToImmutableList();

            var newEvent = oldEvent with { ReportList = newList };

            eventRegistry.Update(newEvent, oldEvent);
        }
    }
}
