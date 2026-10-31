using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost.Logging;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class FileLoggerProviderTests
{
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
