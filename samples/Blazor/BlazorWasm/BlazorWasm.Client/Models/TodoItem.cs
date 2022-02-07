namespace BlazorWasm.Models;

public record class TodoItem
{
    public Guid Key { get; init; }
    public string Title { get; init; } = null!;
    public bool IsDone { get; init; }
    public Guid OwnerKey { get; init; }
}
