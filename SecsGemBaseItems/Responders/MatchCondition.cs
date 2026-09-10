using System;
using System.Linq;
using SecsGemBaseItems.Data_Containers;

namespace SecsGemBaseItems.Responders;

/// <summary>Comparison applied by a <see cref="MatchCondition"/>. Kept intentionally small for v1.</summary>
public enum MatchOperator
{
    Equals,
    NotEquals
}

/// <summary>
/// A single gate on an incoming <see cref="SecsGemDataMessage"/>: the item at <see cref="ItemPath"/>
/// must (or must not) equal <see cref="Value"/>. A responder fires only when every condition passes.
/// A missing path always fails the condition.
/// </summary>
public sealed class MatchCondition
{
    public string ItemPath { get; set; } = string.Empty;
    public MatchOperator Operator { get; set; } = MatchOperator.Equals;
    public string Value { get; set; } = string.Empty;

    public bool Evaluate(SecsGemDataMessage incoming)
    {
        if (!SecsGemItemPath.TryResolve(incoming, ItemPath, out var item))
            return false;

        var actual = string.Join(",", item.GetStringValues()).Trim();
        var expected = (Value ?? string.Empty).Trim();
        var equal = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        return Operator == MatchOperator.Equals ? equal : !equal;
    }

    public override string ToString()
    {
        var op = Operator == MatchOperator.Equals ? "=" : "≠";
        return $"[{ItemPath}] {op} {Value}";
    }
}
