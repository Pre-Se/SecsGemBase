using SecsGemBaseItems.Data_Containers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SecsGemMessageHandling.Events.Models;
using SecsGemMessageHandling.Events.Registry.Interface;

namespace SecsGemMessageHandling.Events.Builders;
public class SecsGemReportBuilder(
    IRegistry<SecsGemReport> reportRegistry,
    IRegistry<SecsGemEquipmentVariable> equipmentVariableRegistry,
    ILogger<SecsGemReportBuilder> logger)
{
    private readonly IRegistry<SecsGemReport> reportRegistry = reportRegistry;
    private readonly IRegistry<SecsGemEquipmentVariable> equipmentVariableRegistry = equipmentVariableRegistry;

    /// <summary>
    /// Builds Report formatted for use in a S6F11 data message
    /// </summary>
    /// <param name="rptid">report id of the report to be build</param>
    /// <param name="reportItem">the built report, if built</param>
    /// <returns>returns true if built successfully, otherwise false</returns>
    public bool TryBuildReport(int rptid, [NotNullWhen(true)] out SecsGemItem? reportItem)
    {
        reportItem = null;
        if (!reportRegistry.TryGet(rptid, out var report))
        {
            logger.LogError("Report [{rptid}] doesn't exist", rptid);
            return false;
        }

        IList<SecsGemItem> reportVariables = [];

        foreach (var variableId in report.Variables)
        {
            if (equipmentVariableRegistry.TryGet(variableId, out var equipmentVariable))
                reportVariables.Add(equipmentVariable.Item);
            else
            {
                logger.LogError("Report [{rptid}] could not be build due to variable [{vid}] not existing", rptid, variableId);
                return false;
            }
        }

        reportItem = new ItemFactory()
            .AddList(reportList => reportList
                .AddI4(rptid, "ReportID")
                .AddList(variableList =>
                {
                    foreach (var reportVariable in reportVariables)
                    {
                        variableList.AddItem(reportVariable);
                    }
                }))
            .Build()[0];

        return true;
    }

    public bool TryBuildReportDefine(int rptid, [NotNullWhen(true)] out SecsGemItem? reportItem)
    {
        reportItem = null;
        if (!reportRegistry.TryGet(rptid, out var report))
        {
            return false;
        }

        reportItem = new ItemFactory()
            .AddList(reportDefine => reportDefine
                    .AddI4(rptid, "RPTID")
                    .AddList(variableList =>
                    {
                        foreach (var variableId in report.Variables)
                        {
                            variableList.AddI4(variableId);
                        }
                    })
                , "Report Define")
            .Build()[0];

        return true;
    }
}
