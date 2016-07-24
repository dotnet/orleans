using System;

namespace Orleans.Streams
{
    public interface IStreamIdentity
    {
        /// <summary> Stream primary key guid. </summary>
        Guid Guid { get; }

        /// <summary> Stream namespace. </summary>
        string Namespace { get; }
    }
}
