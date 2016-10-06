using System;
using System.Diagnostics;
using Orleans.Runtime;

namespace UnitTests
{
    // used for test constants
    internal static class TestConstants
    {
        public static readonly SafeRandom random = new SafeRandom();

        public static readonly TimeSpan InitTimeout =
            Debugger.IsAttached ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1);
        
    }
}
