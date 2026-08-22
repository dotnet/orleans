#nullable enable

using System;
using Orleans.Serialization.Buffers;

namespace Orleans.Connections.Transport;

public abstract class WriteRequest
{
    public ArcBufferReader Buffers { get; protected set; }
    public abstract void SetResult();
    public abstract void SetException(Exception error);
}
