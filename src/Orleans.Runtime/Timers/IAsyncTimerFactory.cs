using System;

namespace Orleans.Runtime
{
    internal interface IAsyncTimerFactory
    {
        IAsyncTimer Create(TimeSpan period, string name);
    }
}
