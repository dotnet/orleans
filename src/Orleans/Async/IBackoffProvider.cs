using System;

namespace Orleans
{
    internal interface IBackoffProvider
    {
        TimeSpan Next(int attempt);
    }
}