using Orleans.Concurrency;

namespace BlazorServer.Models;

[Immutable, Serializable]
public record class TodoNotification(
    Guid ItemKey,
    TodoItem? Item = null);