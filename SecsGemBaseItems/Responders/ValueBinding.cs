using System;
using System.Collections.Generic;
using System.Linq;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Enums;

namespace SecsGemBaseItems.Responders;

/// <summary>Where a <see cref="ValueBinding"/> gets the value it writes into the outgoing message.</summary>
public enum BindingSourceKind
{
    /// <summary><see cref="ValueBinding.SourceRef"/> holds a fixed value (comma/space separated for arrays).</summary>
    Literal,

    /// <summary><see cref="ValueBinding.SourceRef"/> is an item path in the incoming message; its values are copied across.</summary>
    Echo,

    /// <summary><see cref="ValueBinding.SourceRef"/> is an item path in the incoming message; the whole item (a list and all its children, or a scalar) replaces the target node.</summary>
    CopyBranch
}

/// <summary>
/// Sets one item in the outgoing message. Either a literal, or a value/branch pulled from the
/// message that triggered the responder.
/// </summary>
public sealed class ValueBinding
{
    public string TargetItemPath { get; set; } = string.Empty;
    public BindingSourceKind Source { get; set; } = BindingSourceKind.Literal;
    public string SourceRef { get; set; } = string.Empty;

    /// <summary>
    /// Echo / CopyBranch only: id of the upstream Receive node whose captured message this pulls from.
    /// Null or empty means "the most recently received message in the run" (keeps older bindings working).
    /// </summary>
    public string? SourceNodeId { get; set; }

    /// <summary>Applies this binding against a single incoming message. Returns <c>false</c> when a path cannot be resolved.</summary>
    public bool Apply(SecsGemDataMessage outgoing, SecsGemDataMessage? incoming)
        => Apply(outgoing, _ => incoming);

    /// <summary>
    /// Applies this binding. For Echo / CopyBranch, <paramref name="resolveIncoming"/> is called with
    /// <see cref="SourceNodeId"/> and must return the message captured by that Receive node
    /// (or a sensible fallback when the id is null/unknown). Returns <c>false</c> when a path or message
    /// cannot be resolved.
    /// </summary>
    public bool Apply(SecsGemDataMessage outgoing, Func<string?, SecsGemDataMessage?> resolveIncoming)
    {
        switch (Source)
        {
            case BindingSourceKind.Literal:
                if (!SecsGemItemPath.TryResolve(outgoing, TargetItemPath, out var literalTarget))
                    return false;
                literalTarget.SetValuesFromStrings(LiteralValues(literalTarget.FormatType, SourceRef));
                return true;

            case BindingSourceKind.Echo:
            {
                var incoming = resolveIncoming(SourceNodeId);
                if (incoming is null)
                    return false;
                if (!SecsGemItemPath.TryResolve(incoming, SourceRef, out var echoSource))
                    return false;
                if (!SecsGemItemPath.TryResolve(outgoing, TargetItemPath, out var echoTarget))
                    return false;
                echoTarget.SetValuesFromStrings(echoSource.GetStringValues().ToArray());
                return true;
            }

            case BindingSourceKind.CopyBranch:
            {
                var incoming = resolveIncoming(SourceNodeId);
                if (incoming is null)
                    return false;
                if (!SecsGemItemPath.TryResolve(incoming, SourceRef, out var branchSource))
                    return false;
                return SecsGemItemPath.ReplaceNode(outgoing, TargetItemPath, branchSource.Clone());
            }

            default:
                return false;
        }
    }

    private static IEnumerable<string> LiteralValues(SecsGemItemFormatType formatType, string? raw)
    {
        raw ??= string.Empty;
        if (formatType is SecsGemItemFormatType.ASCII or SecsGemItemFormatType.JIS8 or SecsGemItemFormatType.TwoByteCharacter)
            return [raw];

        return raw.Split([',', ';', ' '], System.StringSplitOptions.RemoveEmptyEntries);
    }

    public override string ToString() => Source switch
    {
        BindingSourceKind.Literal => $"[{TargetItemPath}] = \"{SourceRef}\"",
        BindingSourceKind.Echo => $"[{TargetItemPath}] ← incoming[{SourceRef}]",
        BindingSourceKind.CopyBranch => $"[{TargetItemPath}] ⇐ incoming[{SourceRef}] (branch)",
        _ => base.ToString()!
    };
}
