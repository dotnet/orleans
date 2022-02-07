namespace ChatRoom;

[Serializable]
public record class ChatMsg(string? Author, string Text)
{
    public string Author { get; init; } = Author ?? "Alexey";

    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;    
}
