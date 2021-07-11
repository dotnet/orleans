using System;

namespace FasterSample.Core.Clocks
{
    internal class SystemClock : ISystemClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}