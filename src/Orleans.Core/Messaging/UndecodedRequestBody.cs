namespace Orleans.Runtime.Messaging;

/// <summary>
/// Stores the original bytes for a request whose invokable alias is unavailable on this host,
/// allowing version compatibility and placement to route it to a suitable destination.
/// </summary>
internal sealed class UndecodedRequestBody
{
    public UndecodedRequestBody(byte[] body, string? alias)
    {
        Body = body;
        Alias = alias ?? "<unknown alias>";
    }

    public byte[] Body { get; }

    public string Alias { get; }

    public override string ToString() => $"undecoded request with unavailable invokable alias: {Alias}";
}
