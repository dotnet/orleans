namespace Tester.AzureUtils
{
    /// <summary>
    /// DO NOT use this class as a <code>IClassFixture</code> due to https://github.com/AArnott/Xunit.SkippableFact/issues/32
    /// </summary>
    public abstract class AzureStorageBasicTests
    {
        public AzureStorageBasicTests()
        {
            TestUtils.CheckForAzureStorage();
        }
    }
}
