internal readonly record struct ClientContext(
    IClusterClient Client,
    string? UserName = null,
    string? CurrentChannel = null)
{
    /// <summary>
    /// Check if the client is connected to a channel.
    /// </summary>
    public bool IsConnectedToChannel => CurrentChannel is not null;
}
