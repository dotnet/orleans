using Orleans.Concurrency;

namespace BlazorWasm.Models;

[Immutable]
public record class TodoNotification(
    Guid ItemKey,
    TodoItem? Item = null);
