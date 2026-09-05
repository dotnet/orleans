namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Identifies the network protocol version used by a connection.
    /// </summary>
    public enum NetworkProtocolVersion : byte
    {
        /// <summary>
        /// A message body shares the type-reference table built from its headers.
        /// </summary>
        Version1 = 1,

        /// <summary>
        /// A message body has an independent type-reference table, allowing it to be forwarded without decoding.
        /// </summary>
        Version2 = 2,
    }
}
