using System;

namespace FasterSample.Core.Clocks
{
    public interface ISystemClock
    {
        DateTime UtcNow { get; }
    }
}