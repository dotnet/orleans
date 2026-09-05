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

    [Fact]
    public void GetExecutablePath_UppercaseDllExtension_UsesExecutableSibling()
    {
        var fileName = $"StandaloneSiloHandle-{Guid.NewGuid():N}";
        var assemblyPath = Path.Combine(Environment.CurrentDirectory, fileName + ".DLL");
        var executablePath = Path.Combine(Environment.CurrentDirectory, fileName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        File.WriteAllText(assemblyPath, string.Empty);
        File.WriteAllText(executablePath, string.Empty);

        try
        {
            var result = StandaloneSiloHandle.GetExecutablePath(
                typeof(StandaloneSiloHandleTests).Assembly,
                assemblyPath,
                isEntryAssembly: false,
                processPath: null);

            Assert.Equal(executablePath, result);
        }
        finally
        {
            File.Delete(assemblyPath);
            File.Delete(executablePath);
        }
    }
}
