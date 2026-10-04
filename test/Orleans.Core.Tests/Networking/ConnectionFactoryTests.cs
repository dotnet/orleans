using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections.Transport;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace Orleans.Core.Tests.Networking;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class ConnectionFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDisposal_DisposesDecoratorChainAndRegisteredConnectorOnce(bool asynchronous)
    {
        var events = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<MessageTransportConnector>(_ => new TrackingConnector(events));
        services.AddSingleton<IMessageTransportConnectorMiddleware>(_ => new TrackingMiddleware("first", events));
        services.AddSingleton<IMessageTransportConnectorMiddleware>(_ => new TrackingMiddleware("second", events));
        services.AddSingleton<ConnectionFactory, TestConnectionFactory>();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ConnectionFactory>();
        var connector = provider.GetRequiredService<MessageTransportConnector>();

        if (asynchronous)
        {
            await provider.DisposeAsync();
        }
        else
        {
            provider.Dispose();
        }

        await factory.DisposeAsync();
        factory.Dispose();
        Assert.Equal(["second", "first", "registered"], events);
        Assert.Equal(1, Assert.IsType<TrackingConnector>(connector).DisposeCount);
    }

    [Fact]
    public async Task Disposal_DrainsAdmittedAttemptAndRejectsNewAttempts()
    {
        var events = new List<string>();
        var connector = new TrackingConnector(events);
        var factory = new TestConnectionFactory(connector, [new TrackingMiddleware("decorator", events)]);
        var address = SiloAddress.New(IPAddress.Loopback, 12345, 1);
        var attempt = factory.ConnectAsync(address, TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(1, connector.AttemptCount);
        Assert.Equal(address.Endpoint, connector.Endpoint);
        Assert.Equal(TestContext.Current.CancellationToken, connector.CancellationToken);
        var disposal = factory.DisposeAsync().AsTask();
        var repeatedDisposal = factory.DisposeAsync().AsTask();
        Assert.Same(disposal, repeatedDisposal);
        Assert.False(disposal.IsCompleted);
        Assert.Empty(events);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => factory.ConnectAsync(address, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, connector.AttemptCount);
        var failure = new InvalidOperationException("Connect failed");
        connector.Completion.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => attempt));
        await disposal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["decorator"], events);
        Assert.Equal(0, connector.DisposeCount);
        await connector.DisposeAsync();
        Assert.Equal(["decorator", "registered"], events);
    }

    private sealed class TestConnectionFactory(
        MessageTransportConnector connector,
        IEnumerable<IMessageTransportConnectorMiddleware> middleware) : ConnectionFactory(connector, middleware)
    {
        protected override Connection CreateConnection(SiloAddress address, MessageTransport context) =>
            throw new InvalidOperationException("This test only creates failed connection attempts.");

        protected override EndPoint GetEndPoint(SiloAddress address) => address.Endpoint;
    }

    private sealed class TrackingConnector(List<string> events) : MessageTransportConnector, IDisposable
    {
        public TaskCompletionSource<MessageTransport> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AttemptCount { get; private set; }
        public int DisposeCount { get; private set; }
        public EndPoint? Endpoint { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool IsValid => true;

        public override ValueTask<MessageTransport> CreateAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            Endpoint = endpoint;
            CancellationToken = cancellationToken;
            return new(Completion.Task);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add("registered");
            return base.DisposeAsync();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class TrackingMiddleware(string name, List<string> events) : IMessageTransportConnectorMiddleware
    {
        public MessageTransportConnector Apply(MessageTransportConnector transport) => new TrackingDecorator(transport, name, events);
    }

    private sealed class TrackingDecorator(
        MessageTransportConnector inner,
        string name,
        List<string> events) : MessageTransportConnector
    {
        public override IFeatureCollection Features => inner.Features;
        public override bool IsValid => inner.IsValid;
        public override ValueTask<MessageTransport> CreateAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(endpoint, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            events.Add(name);
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
