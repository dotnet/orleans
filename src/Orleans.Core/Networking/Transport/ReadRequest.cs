#nullable enable

using System;
using Orleans.Serialization.Buffers;

namespace Orleans.Connections.Transport;

public abstract class ReadRequest
{
    public abstract bool OnRead(ArcBufferReader buffer);
    public abstract void OnError(Exception error);
    public abstract void OnCanceled();
}
