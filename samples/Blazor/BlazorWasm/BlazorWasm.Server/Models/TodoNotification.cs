namespace BlazorWasm.Models;

[Immutable]
[GenerateSerializer]
public record class TodoNotification(
    Guid ItemKey,
    TodoItem? Item = null);
