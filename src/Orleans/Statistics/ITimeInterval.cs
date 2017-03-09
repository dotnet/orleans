using System;

namespace Orleans.Runtime
{
    internal interface ITimeInterval
    {
        void Start();

        void Stop();

        void Restart();

        TimeSpan Elapsed { get; }
    }
}