using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public static class StorageEmulatorUtilities
    {
        public static void EnsureEmulatorIsNotUsed()
        {
            if (AzuriteContainerManager.ConnectionString is { Length: > 0 } connectionString
                && (connectionString.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("devstoreaccount", StringComparison.OrdinalIgnoreCase)))
            {
                throw new SkipException("This test does not support the storage emulator.");
            }
        }
    }
}
