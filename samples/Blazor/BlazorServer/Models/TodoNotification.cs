using Orleans;
using Orleans.Concurrency;

namespace BlazorServer.Models;

[Immutable]
[GenerateSerializer]
public record class TodoNotification(
    Guid ItemKey,
    TodoItem? Item = null);