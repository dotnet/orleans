using System;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public static class StorageEmulatorUtilities
    {
        public static void EnsureEmulatorIsNotUsed()
        {
            if (TestDefaultConfiguration.DataConnectionString is { Length: > 0 } connectionString && connectionString.IndexOf("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new SkipException("This test does not support the storage emulator.");
            }
        }
    }
}
