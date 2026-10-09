# ColdChainMonitor — Assignment 3

## Stream Ownership and Helpers
The caller (e.g., `Program.cs` or a unit test) owns all `Stream` instances. Helper methods like `ReadReadings`, `WriteArchive`, and `ReadArchive` must leave supplied streams open (`leaveOpen: true`) so that the caller can perform further operations, such as reading from a different position or piping streams.

## Lazy Sequences and Reader Disposal
Returning a lazy sequence (such as `IEnumerable<T>` using `yield return`) that depends on a `StreamReader` is unsafe because the sequence is evaluated on demand. If the reader is disposed upon exiting the helper method, attempting to enumerate the sequence later will throw an `ObjectDisposedException`. Materializing the sequence (e.g., calling `.ToList()`) before returning resolves this issue.

## Resetting MemoryStream Position
When data is written to a `MemoryStream` via serialization, the stream's position indicator advances to the end of the written content (`Position = Length`). Before reading or deserializing from the same stream, `MemoryStream.Position` must be reset to `0` so the reader reads from the beginning rather than reading zero bytes at the end of the stream.

## Grouping Readings Before Pairwise Comparison
Readings in the input file may arrive out of chronological order or interspersed across multiple sensors. Grouping readings by `SensorId` before comparing adjacent pairs ensures that temperature differences and range checks are calculated strictly between consecutive readings of the exact same sensor.

## Enum.TryParse and Undefined Numeric Values
In C#, enumerations are backed by integral types. `Enum.TryParse` successfully parses string representations of numbers (e.g., `"99"`) into enum values even if those numbers are not defined members of the `enum`. To strictly reject undefined numeric enum values, `Enum.IsDefined` must be used alongside `Enum.TryParse`.

## Pipeline Purity and I/O Separation
- **Pure Components**: `TryParseReading`, `Analyze`, and core domain record instantiations. These functions operate strictly on input arguments without reading files, modifying global state, or calling system time.
- **I/O Components**: `ReadReadings`, `WriteArchive`, `ReadArchive`, and file operations inside `Program.Main`.

## Value Equality in JSON Serialization
JSON serialization and deserialization reconstruct objects in memory, resulting in new object instances with distinct reference identities. Testing equality by value (which `record` types provide natively via `Equals`) verifies that data contents and collection elements are perfectly preserved across the round trip regardless of memory references.

## Deterministic Ordering and Injected Timestamps
- **Injected `CreatedAtUtc`**: Injecting `CreatedAtUtc` from the caller eliminates hidden dependencies on system clocks (`DateTime.Now`), making parsing and archiving logic completely deterministic and testable.
- **Deterministic Ordering**: Ordering alerts strictly by `SensorId` (case-insensitive) and then by `Timestamp` guarantees consistent output across all environments and test runs.