using Orleans;

internal readonly record struct ClientContext(
    IClusterClient Client,
    string? UserName = null,
    string? CurrentChannel = null);