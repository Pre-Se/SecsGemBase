using System.Collections.Concurrent;
using SecsGemBaseItems.Data_Containers;
using SecsGemMessageHandling.Events.Models;

namespace SecsGemMessageHandling.Events;

public class SecsGemLinkEventReportBuilder
{
    public ConcurrentDictionary<SecsGemEventReport, IList<SecsGemReport>> LinkEventReportDictionary = [];
    public SecsGemDataMessage CreateEventReportLink()
    {
        var linkEventReportMessage = new MessageFactory(2, 35, true, "Link Event Report (LER)", new ItemFactory()
                .AddList(l1 => l1
                    .AddU4(1000, "Data ID")
                    .AddList(events =>
                    {
                        foreach (var (secGemEvent, reports) in LinkEventReportDictionary)
                        {
                            events.AddList(l2 => l2
                                    .AddI4(secGemEvent.Ceid, $"CEID [{secGemEvent.EventName}]"))
                                    .AddList(l3 =>
                                    {
                                        foreach (var report in reports)
                                        {
                                            l3.AddI4(report.Rptid, $"RPTID [{report.ReportName}]");
                                        }
                                    });
                        }
                    })))
            .Build();

        return linkEventReportMessage;
    }
}