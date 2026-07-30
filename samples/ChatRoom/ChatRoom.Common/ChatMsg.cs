namespace ChatRoom;

[GenerateSerializer, Immutable]
public record class ChatMsg(
    string? Author,
    string Text)
{
    [Id(0)]
    public string Author { get; init; } = Author ?? "Anonymous";

    [Id(1)]
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
}
