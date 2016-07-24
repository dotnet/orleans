using System;
using Orleans.Providers;

namespace Orleans.Streams
{
    public enum StreamProviderDirection
    {
        None,
        ReadOnly,
        WriteOnly,
        ReadWrite
    }
}
