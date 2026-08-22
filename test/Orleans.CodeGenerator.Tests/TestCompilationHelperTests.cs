using System.IO;

namespace Orleans.CodeGenerator.Tests;

public class TestCompilationHelperTests
{
    [Fact]
    public void GetFrameworkAssemblyPathsUsesPlatformPathComparer()
    {
        var frameworkDirectory = Path.Combine(Path.GetTempPath(), "Framework");
        var frameworkAssemblyPath = Path.Combine(frameworkDirectory, "System.Private.CoreLib.dll");
        var matchingDirectory = OperatingSystem.IsWindows() ? frameworkDirectory.ToUpperInvariant() : frameworkDirectory;
        var matchingAssemblyPath = Path.Combine(matchingDirectory, "System.Runtime.dll");
        var otherAssemblyPath = Path.Combine(Path.GetTempPath(), "Other", "System.Runtime.dll");
        var runtimeAssemblyPath = Path.Combine(Path.GetTempPath(), "Packages", "System.Runtime.dll");
        var trustedPlatformAssemblies = $"{matchingAssemblyPath}{Path.PathSeparator}{otherAssemblyPath}";

        var result = TestCompilationHelper.GetFrameworkAssemblyPaths(
            trustedPlatformAssemblies,
            frameworkAssemblyPath,
            runtimeAssemblyPath);

        Assert.Equal(runtimeAssemblyPath, Assert.Single(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("System.Private.CoreLib.dll")]
    public void GetFrameworkAssemblyPathsRequiresFrameworkDirectory(string frameworkAssemblyPath)
    {
        var trustedPlatformAssemblies = Path.Combine(Path.GetTempPath(), "Framework", "System.Runtime.dll");

        var exception = Assert.Throws<InvalidOperationException>(
            () => TestCompilationHelper.GetFrameworkAssemblyPaths(
                trustedPlatformAssemblies,
                frameworkAssemblyPath));

        Assert.Equal("The test host framework directory must be available.", exception.Message);
    }

    [Fact]
    public void GetFrameworkAssemblyPathsRequiresMatchingReferences()
    {
        var frameworkAssemblyPath = Path.Combine(
            Path.GetTempPath(),
            "Framework",
            "System.Private.CoreLib.dll");
        var trustedPlatformAssemblies = Path.Combine(Path.GetTempPath(), "Other", "System.Runtime.dll");

        var exception = Assert.Throws<InvalidOperationException>(
            () => TestCompilationHelper.GetFrameworkAssemblyPaths(
                trustedPlatformAssemblies,
                frameworkAssemblyPath));

        Assert.StartsWith("The trusted platform assemblies must include references from", exception.Message);
    }
}
