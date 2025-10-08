using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Streams;

namespace Orleans.Streaming;

[Serializable]
[GenerateSerializer]
public sealed class OldestInStreamToken : StreamSequenceToken
{
    /// <summary>
    /// Always -1, which is less than any other valid sequence number.
    /// </summary>
    public override long SequenceNumber { get => -1; protected set { } }
    public override int EventIndex { get => 0; protected set { } }

    /// <summary>
    /// An instance of the <see cref="OldestInStreamToken"/> class.
    /// </summary>
    public static OldestInStreamToken Instance { get; } = new OldestInStreamToken();

    public override bool Equals(StreamSequenceToken other)
    {
        return other is OldestInStreamToken;
    }

    public override int CompareTo(StreamSequenceToken other)
    {
        if (other is OldestInStreamToken) return 0;
        return -1;
    }
}

