# SECS/GEM Item Parsing and Display

## SecsGemItem internals

`SecsGemItem` stores its value(s) in `ObservableCollection<string> Values`. For non-list types, each element in `Values` represents one parsed element of the item (e.g., a multi-byte binary item has one string per byte).

**Binary items** are stored internally as base-2 strings — e.g., byte `0x05` is stored as `"101"`. This is the format expected by `ConvertValueAtIndexToBytes` when serialising outgoing messages (`Convert.ToByte(value, 2)`). Do not change this internal representation.

**Display** (`SetName` → `Name` → `Header`) converts binary values to hex for readability: `0x05`, `0x0A`, etc. All bytes are shown joined by spaces. Other format types show only the first value.

`Header` is computed in `DataItem.SetHeader`: `"{Name} - {Description}"` if a description exists, otherwise just `Name`. It is what the log window TreeView binds to.

## Parsing pipeline

`MessageParsing` reads raw bytes starting at offset 14 (past the HSMS message header). For each item:

1. Read format byte → `SecsGemItemFormatType` (upper 6 bits) and number of length bytes (lower 2 bits).
2. Read length bytes → item payload length in bytes.
3. Dispatch:
   - **List** — recurse for each child.
   - **ASCII** — copy bytes directly as a single string into `Values[0]`.
   - **All other types** — call `ReadBasicFormatValues`.

### `ReadBasicFormatValues` (batch pattern)

All element values are read into a local `List<string>` buffer first, then assigned to `item.Values` as a single new `ObservableCollection<string>`. This is intentional: `Values.CollectionChanged` is wired to `SetName`, so adding elements one by one would trigger `SetName` once per element — O(n²) for large items. The batch assignment fires `PropertyChanged` for `Values` exactly once, triggering `SetName` once regardless of item size.

## HSMS T7 timer and `IgnoreState`

The T7 timer fires if the `NotSelected` state is not exited within the configured timeout. Two places check `HSMSParameters.IgnoreState` before acting:

- `StartT7Timer` — skips starting the timer entirely.
- The T7 timer callback — returns immediately without logging or triggering a `CommunicationFailure` state change.

This allows connections to remain open in passive/diagnostic setups where the select handshake is not expected.
