using System.Collections.Immutable;
using System.Numerics;
using SecsGemBaseItems.Data_Containers;
using SecsGemMessageHandling.Events.Interfaces;

namespace SecsGemMessageHandling.Events.Models;

/// <summary>
/// Used to build an S6F11 Event Report Send <see cref="SecsGemDataMessage"/>
/// </summary>
public record SecsGemEventReport : ISecsGemEventReport, IEqualityOperators<SecsGemEventReport, SecsGemEventReport, bool>, IKeyedItem
{
    public required int Ceid { get; init; }
    public string EventName { get; init; } = string.Empty;
    public ImmutableList<int> ReportList { get; init; } = [];
    public bool IsActive { get; init; } = false;
    public int Id => Ceid;
    public static string LogPrefix => "Event with CEID";
}