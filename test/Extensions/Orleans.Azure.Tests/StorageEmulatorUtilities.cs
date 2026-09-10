using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public static class StorageEmulatorUtilities
    {
        public static void EnsureEmulatorIsNotUsed()
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                return;
            }

            if (TestDefaultConfiguration.AzureStorageConnectionString is { Length: > 0 } connectionString
                && (connectionString.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("devstoreaccount", StringComparison.OrdinalIgnoreCase)))
            {
                throw Xunit.Sdk.SkipException.ForSkip("This test does not support the storage emulator.");
            }
        }
    }
}
