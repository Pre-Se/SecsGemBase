using System.Collections.Generic;
using System.Linq;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;

namespace SecsGemBaseItems.Responders;

/// <summary>
/// Addresses a single <see cref="SecsGemItem"/> inside a <see cref="SecsGemDataMessage"/> tree by a
/// positional index path (dot separated, e.g. <c>"1"</c> or <c>"2.0.1.0"</c>). The path walks the
/// message's <see cref="DataItem.Children"/> collection: segment 0 indexes the message body,
/// each following segment indexes into the previous item's children.
/// </summary>
public static class SecsGemItemPath
{
    private static readonly char[] Separators = ['.', '/'];

    /// <summary>Appends <paramref name="index"/> to <paramref name="parentPath"/> (which may be empty for a root child).</summary>
    public static string Combine(string? parentPath, int index)
        => string.IsNullOrEmpty(parentPath) ? index.ToString() : $"{parentPath}.{index}";

    /// <summary>Resolves the item addressed by <paramref name="path"/>, or returns <c>false</c> when any segment is missing.</summary>
    public static bool TryResolve(SecsGemDataMessage? root, string? path, out SecsGemItem item)
    {
        item = null!;
        if (root is null || string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Split(Separators, System.StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        IList<IDataItem> children = root.Children;
        SecsGemItem? current = null;

        foreach (var segment in segments)
        {
            if (!int.TryParse(segment, out var index) || index < 0 || index >= children.Count)
                return false;
            if (children[index] is not SecsGemItem next)
                return false;
            current = next;
            children = current.Children;
        }

        if (current is null)
            return false;

        item = current;
        return true;
    }

    /// <summary>
    /// Resolves the container (message body or list item children) that holds the final path segment,
    /// plus the index of the addressed item within it.
    /// </summary>
    public static bool TryResolveParent(SecsGemDataMessage? root, string? path, out IList<IDataItem> siblings, out int index)
    {
        siblings = null!;
        index = -1;
        if (root is null || string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Split(Separators, System.StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        IList<IDataItem> children = root.Children;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out var segIndex) || segIndex < 0 || segIndex >= children.Count)
                return false;

            if (i == segments.Length - 1)
            {
                siblings = children;
                index = segIndex;
                return true;
            }

            if (children[segIndex] is not SecsGemItem next)
                return false;
            children = next.Children;
        }

        return false;
    }

    /// <summary>String representation of every value the item currently holds.</summary>
    public static string[] ReadValues(SecsGemItem item) => item.GetStringValues().ToArray();

    /// <summary>Replaces every value of <paramref name="item"/> from the supplied strings, parsed for its format.</summary>
    public static void WriteValues(SecsGemItem item, IEnumerable<string> values) => item.SetValuesFromStrings(values);

    /// <summary>
    /// Swaps the item at <paramref name="path"/> for <paramref name="replacement"/>, keeping its position and
    /// re-parenting the replacement so tree/name bookkeeping stays consistent.
    /// </summary>
    public static bool ReplaceNode(SecsGemDataMessage root, string? path, SecsGemItem replacement)
    {
        if (!TryResolveParent(root, path, out var siblings, out var index))
            return false;

        var existing = siblings[index] as SecsGemItem;
        replacement.Parent = existing?.Parent;
        siblings[index] = replacement;
        return true;
    }
}
