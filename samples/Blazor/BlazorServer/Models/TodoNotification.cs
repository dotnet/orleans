using Orleans;
using Orleans.Concurrency;

namespace BlazorServer.Models;

[Immutable, Serializable]
[GenerateSerializer]
public record class TodoNotification(
    Guid ItemKey,
    TodoItem? Item = null);