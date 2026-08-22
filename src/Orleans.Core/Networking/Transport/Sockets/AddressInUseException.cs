#nullable enable
using System;

namespace Orleans.Connections.Transport.Sockets;

[Serializable]
public class AddressInUseException : Exception
{
    public AddressInUseException()
    {
    }

    public AddressInUseException(string? message) : base(message)
    {
    }

    public AddressInUseException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}