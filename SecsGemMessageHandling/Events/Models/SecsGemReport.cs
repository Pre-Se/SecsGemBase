using System.Collections.Immutable;
using System.Numerics;

namespace SecsGemMessageHandling.Events.Models;
public record SecsGemReport : IEqualityOperators<SecsGemReport, SecsGemReport, bool>, IKeyedItem
{
    public required int Rptid { get; init; }
    public string ReportName { get; init; } = string.Empty;
    public ImmutableList<int> Variables { get; init; } = [];
    public int Id => Rptid;
    public static string LogPrefix => "Report with ID";
}
