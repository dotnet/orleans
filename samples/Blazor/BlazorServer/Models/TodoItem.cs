using Orleans;
using Orleans.Concurrency;

namespace BlazorServer.Models;

[Immutable]
[GenerateSerializer]
public record class TodoItem(
    Guid Key,
    string Title,
    bool IsDone,
    Guid OwnerKey,
    DateTime? Timestamp = null);