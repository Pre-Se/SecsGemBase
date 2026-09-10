using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecsGemBaseItems.Responders;

/// <summary>
/// Serialization helpers for the condition / binding lists a scenario node stores as JSON strings
/// (<c>ScenarioNode.MatchConditionsJson</c> / <c>ResponseBindingsJson</c>). Both are plain POCO lists
/// with primitive fields, so <see cref="System.Text.Json"/> handles them once enums are written as names.
/// </summary>
public static class ResponderJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeConditions(IEnumerable<MatchCondition> conditions)
        => JsonSerializer.Serialize(conditions, Options);

    public static List<MatchCondition> DeserializeConditions(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<MatchCondition>>(json, Options) ?? [];

    public static string SerializeBindings(IEnumerable<ValueBinding> bindings)
        => JsonSerializer.Serialize(bindings, Options);

    public static List<ValueBinding> DeserializeBindings(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<ValueBinding>>(json, Options) ?? [];
}
