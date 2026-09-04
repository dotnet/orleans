using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class StandaloneSiloHandleTests
{
    [Fact]
    public void GetExecutablePath_BundledEntryAssembly_UsesProcessPath()
    {
        var processPath = Path.GetTempFileName();

        try
        {
            var result = StandaloneSiloHandle.GetExecutablePath(
                typeof(StandaloneSiloHandleTests).Assembly,
                assemblyLocation: string.Empty,
                isEntryAssembly: true,
                processPath);

            Assert.Equal(processPath, result);
        }
        finally
        {
            File.Delete(processPath);
        }
    }
}
