using Orleans.Concurrency;

namespace BlazorWasm.Models;

[Immutable]
public record class TodoItem(
    Guid Key,
    string Title,
    bool IsDone,
    Guid OwnerKey,
    DateTime? Timestamp = null);
