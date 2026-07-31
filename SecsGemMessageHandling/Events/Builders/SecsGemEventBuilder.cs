using Microsoft.Extensions.Logging;
using SecsGemBaseItems.Data_Containers;
using SecsGemMessageHandling.Events.Models;
using SecsGemMessageHandling.Events.Registry.Interface;
using System.Diagnostics.CodeAnalysis;

namespace SecsGemMessageHandling.Events.Builders;
public class SecsGemEventBuilder(IRegistry<SecsGemEventReport> eventRegistry, SecsGemReportBuilder reportBuilder, ILogger<SecsGemEventBuilder> logger)
{
    private readonly IRegistry<SecsGemEventReport> eventRegistry = eventRegistry;
    private readonly SecsGemReportBuilder reportHandler = reportBuilder;

    /// <summary>
    /// Builds a S6F11 event report data message
    /// </summary>
    /// <param name="ceid">collection event ID to be built</param>
    /// <param name="eventMessage">the built event message</param>
    /// <returns>returns true if built successfully, otherwise false</returns>
    public bool TryBuildEventReportMessage(int ceid, [NotNullWhen(true)]out SecsGemDataMessage? eventMessage)
    {
        eventMessage = null;
        if (!eventRegistry.TryGet(ceid, out var eventReport))
        {
            logger.LogInformation("Event [{ceid}] doesn't exist", ceid);
            return false;
        }

        IList<SecsGemItem> reportList = [];
        foreach (var rptid in eventReport.ReportList)
        {
            if (reportHandler.TryBuildReport(rptid, out var reportItem))
                reportList.Add(reportItem);
            else
                return false;
        }

        eventMessage = new MessageFactory(6, 11, true, $"Event Report [{eventReport.EventName}]", new ItemFactory()
                .AddList(l1 => l1
                    .AddU4(1000, "Data ID")
                    .AddI4(ceid, "CEID")
                    .AddList(reportGroups =>
                    {
                        foreach (var report in reportList)
                        {
                            reportGroups.AddItem(report);
                        }
                    }, "Report Groups")))
            .Build();

        return true;
    }
}
