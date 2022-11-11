namespace ChatRoom;

[GenerateSerializer]
public record class ChatMsg(
    string? Author,
    string Text)
{
    [Id(2)]
    public string Author { get; init; } = Author ?? "Alexey";

    [Id(3)]
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;    
}
