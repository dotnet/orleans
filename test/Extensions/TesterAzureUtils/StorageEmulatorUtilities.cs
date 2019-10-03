using System;
using Microsoft.Azure.Storage;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public static class StorageEmulatorUtilities
    {
        public static void EnsureEmulatorIsNotUsed()
        {
            if (CloudStorageAccount.DevelopmentStorageAccount.ToString().Equals(TestDefaultConfiguration.DataConnectionString, StringComparison.OrdinalIgnoreCase))
            {
                throw new SkipException("This test does not support the storage emulator.");
            }
        }
    }
}
