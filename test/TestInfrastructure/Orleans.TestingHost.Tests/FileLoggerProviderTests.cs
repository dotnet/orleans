using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost.Logging;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class FileLoggerProviderTests
{
    [Fact]
    public void DefaultLoggerFactory_DisposalClosesAndFlushesOwnedFileProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orleans-default-logger-{Guid.NewGuid():N}.log");
        using var factory = TestingUtils.CreateDefaultLoggerFactory(path);
        try
        {
            factory.CreateLogger("factory lifetime").LogInformation("factory logging lifetime marker");
            factory.Dispose();

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var reader = new StreamReader(stream);
            Assert.Contains("factory logging lifetime marker", reader.ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            factory.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultLoggerFactory_InvalidPath_PropagatesConstructionFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"orleans-missing-log-directory-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "test.log");

        Assert.Throws<DirectoryNotFoundException>(() => TestingUtils.CreateDefaultLoggerFactory(path));

        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AddFile_ClosesAndFlushesOutputWhenContainerIsDisposed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orleans-file-logger-{Guid.NewGuid():N}.log");
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddFile(path));
        using var container = services.BuildServiceProvider();
        var provider = Assert.Single(container.GetServices<ILoggerProvider>().OfType<FileLoggerProvider>());
        try
        {
            var logger = container.GetRequiredService<ILogger<FileLoggerProviderTests>>();
            logger.LogInformation("file logging lifetime marker");
            container.Dispose();

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var reader = new StreamReader(stream);
            Assert.Contains("file logging lifetime marker", reader.ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            provider.Dispose();
            File.Delete(path);
        }
    }
}
