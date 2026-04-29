namespace Orleans.Journaling;

/// <summary>
/// Well-known state machine log format keys.
/// </summary>
public static class LogFormatKeys
{
    /// <summary>
    /// The built-in Orleans binary log format.
    /// </summary>
    public const string OrleansBinary = "orleans-binary";

    /// <summary>
    /// The JSON Lines log format.
    /// </summary>
    public const string Json = "json";

    /// <summary>
    /// The protobuf log format.
    /// </summary>
    public const string Protobuf = "protobuf";

    /// <summary>
    /// The MessagePack log format.
    /// </summary>
    public const string MessagePack = "messagepack";
}
