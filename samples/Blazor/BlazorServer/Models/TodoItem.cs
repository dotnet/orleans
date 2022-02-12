using Orleans.Concurrency;

namespace BlazorServer.Models;

[Immutable, Serializable]
public record class TodoItem(
    Guid Key,
    string Title,
    bool IsDone,
    Guid OwnerKey,
    DateTime? Timestamp = null);