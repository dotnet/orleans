using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.TestingHost.UnixSocketTransport;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
public class UnixSocketTransportListenerTests
{
    [Fact]
    public async Task DisposeRemovesSocketFile()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unix domain sockets are not supported.");
        }

        var path = Path.Combine(Path.GetTempPath(), $"orleans-{Guid.NewGuid():N}.sock");
        var listenerName = "test";
        var options = new StaticOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions>(
            listenerName,
            new UnixDomainSocketMessageTransportListenerOptions { Path = path });
        var listener = new UnixDomainSocketMessageTransportListener(listenerName, options, NullLoggerFactory.Instance);

        try
        {
            await listener.BindAsync(TestContext.Current.CancellationToken);
            Assert.True(File.Exists(path));
        }
        finally
        {
            await listener.DisposeAsync();
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task UnbindCompletesPendingAccept()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unix domain sockets are not supported.");
        }

        var path = Path.Combine(Path.GetTempPath(), $"orleans-{Guid.NewGuid():N}.sock");
        var listenerName = "test";
        var options = new StaticOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions>(
            listenerName,
            new UnixDomainSocketMessageTransportListenerOptions { Path = path });
        await using var listener = new UnixDomainSocketMessageTransportListener(listenerName, options, NullLoggerFactory.Instance);
        await listener.BindAsync(TestContext.Current.CancellationToken);
        var acceptTask = listener.AcceptAsync(TestContext.Current.CancellationToken).AsTask();

        await listener.UnbindAsync(TestContext.Current.CancellationToken);

        Assert.Null(await acceptTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    private sealed class StaticOptionsMonitor<TOptions>(string name, TOptions value) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue => value;

        public TOptions Get(string? requestedName) =>
            requestedName == name ? value : throw new InvalidOperationException($"No options are configured for '{requestedName}'.");

        public IDisposable OnChange(Action<TOptions, string?> listener) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
