namespace BlazorWasm.Models;

[Immutable]
[GenerateSerializer]
public record class TodoItem(
    Guid Key,
    string Title,
    bool IsDone,
    Guid OwnerKey,
    DateTime? Timestamp = null);
