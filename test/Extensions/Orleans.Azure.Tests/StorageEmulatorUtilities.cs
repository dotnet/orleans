using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public static class StorageEmulatorUtilities
    {
        public static void EnsureEmulatorIsNotUsed()
        {
            var skipReason = GetUnsupportedEmulatorSkipReason(
                TestDefaultConfiguration.UseAadAuthentication,
                TestDefaultConfiguration.UseAzurite,
                TestDefaultConfiguration.DataConnectionString);
            if (skipReason is not null)
            {
                throw Xunit.Sdk.SkipException.ForSkip(skipReason);
            }
        }

        internal static string? GetUnsupportedEmulatorSkipReason(
            bool useAadAuthentication,
            bool useAzurite,
            string? dataConnectionString)
        {
            if (useAadAuthentication)
            {
                return null;
            }

            if (useAzurite)
            {
                return "This test does not support Azurite.";
            }

            return dataConnectionString is { Length: > 0 } connectionString
                && (connectionString.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("devstoreaccount", StringComparison.OrdinalIgnoreCase))
                    ? "This test does not support the storage emulator."
                    : null;
        }
    }
}
