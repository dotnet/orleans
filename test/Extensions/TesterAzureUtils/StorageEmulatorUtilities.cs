using System;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public static class StorageEmulatorUtilities
    {
        public static void EnsureEmulatorIsNotUsed()
        {
            if (TestDefaultConfiguration.DataConnectionString is { Length: > 0 } connectionString && connectionString.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase))
            {
                throw new SkipException("This test does not support the storage emulator.");
            }
        }
    }
}
