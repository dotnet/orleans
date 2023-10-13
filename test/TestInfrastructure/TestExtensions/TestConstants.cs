using System.Diagnostics;

namespace TestExtensions
{
    // used for test constants
    internal static class TestConstants
    {
        public static readonly TimeSpan InitTimeout =
            Debugger.IsAttached ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1);
    }
}
