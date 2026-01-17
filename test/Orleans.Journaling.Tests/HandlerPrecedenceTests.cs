using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for handler registration order and precedence in DurableInbox.
/// Verifies that first-match-wins semantics work correctly with multiple handlers
/// and that specific handlers are evaluated before generic prefix handlers.
/// </summary>
[TestCategory("BVT"), TestCategory("Functional"), TestCategory("Journaling")]
public class HandlerPrecedenceTests : IClassFixture<HandlerPrecedenceTests.Fixture>
{
    private readonly Fixture _fixture;

    public HandlerPrecedenceTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Tests that when multiple handlers can handle the same message,
    /// the first registered handler wins.
    /// </summary>
    [Fact]
    public async Task FirstMatchWins_WithMultipleMatchingHandlers()
    {
        // Arrange
        var grain = _fixture.Client.GetGrain<IMultiHandlerGrain>(Guid.NewGuid());
        var senderGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());

        // Act - Send message that matches multiple handlers
        await senderGrain.SendMessage(
            grain.GetGrainId(),
            "test/operation",
            new TestMessage { Content = "multi-match test" });

        // Wait for processing
        await Task.Delay(500);

        // Assert - First handler should have processed it
        var counts = await grain.GetHandlerCounts();
        Assert.Equal(1, counts.Handler1Count);
        Assert.Equal(0, counts.Handler2Count);
        Assert.Equal(0, counts.Handler3Count);
    }

    /// <summary>
    /// Tests that specific RouteKeyHandler is matched before generic RoutePrefixHandler.
    /// </summary>
    [Fact]
    public async Task SpecificHandlerBeforePrefixHandler()
    {
        // Arrange
        var grain = _fixture.Client.GetGrain<ISpecificBeforePrefixGrain>(Guid.NewGuid());
        var senderGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());

        // Act - Send message to specific route that also matches prefix
        await senderGrain.SendMessage(
            grain.GetGrainId(),
            "api/v2/special",
            new TestMessage { Content = "specific route" });

        // Wait for processing
        await Task.Delay(500);

        // Assert - Specific handler should have processed it
        var counts = await grain.GetHandlerCounts();
        Assert.Equal(1, counts.SpecificCount);
        Assert.Equal(0, counts.PrefixCount);

        // Act - Send message to non-specific route that matches prefix
        await senderGrain.SendMessage(
            grain.GetGrainId(),
            "api/v2/general",
            new TestMessage { Content = "prefix route" });

        // Wait for processing
        await Task.Delay(500);

        // Assert - Prefix handler should have processed it
        counts = await grain.GetHandlerCounts();
        Assert.Equal(1, counts.SpecificCount);
        Assert.Equal(1, counts.PrefixCount);
    }

    /// <summary>
    /// Tests that handler registration order determines precedence.
    /// Verifies that changing registration order changes which handler processes a message.
    /// </summary>
    [Fact]
    public async Task RegistrationOrderAffectsDispatch()
    {
        // Arrange - Test with handler A registered first
        var grainA = _fixture.Client.GetGrain<IOrderedHandlerGrainA>(Guid.NewGuid());
        var senderGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());

        // Act
        await senderGrain.SendMessage(
            grainA.GetGrainId(),
            "order/test",
            new TestMessage { Content = "order test A" });

        // Wait for processing
        await Task.Delay(500);

        // Assert - Handler A should have processed it
        var countsA = await grainA.GetHandlerCounts();
        Assert.Equal(1, countsA.HandlerACount);
        Assert.Equal(0, countsA.HandlerBCount);

        // Arrange - Test with handler B registered first (different grain type)
        var grainB = _fixture.Client.GetGrain<IOrderedHandlerGrainB>(Guid.NewGuid());

        // Act
        await senderGrain.SendMessage(
            grainB.GetGrainId(),
            "order/test",
            new TestMessage { Content = "order test B" });

        // Wait for processing
        await Task.Delay(500);

        // Assert - Handler B should have processed it
        var countsB = await grainB.GetHandlerCounts();
        Assert.Equal(0, countsB.HandlerACount);
        Assert.Equal(1, countsB.HandlerBCount);
    }

    /// <summary>
    /// Test fixture that configures the cluster with durable messaging.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    opts.MaxCapacity = 100;
                    opts.DeduplicationWindow = TimeSpan.FromDays(7);
                });
            });
        }
    }
}

// ============================================================================
// Test Message Types  
// ============================================================================
// Note: TestMessage is defined in DurableMessagingRecoveryTests.cs and reused here

[GenerateSerializer]
public record HandlerCounts
{
    [Id(0)] public int Handler1Count { get; init; }
    [Id(1)] public int Handler2Count { get; init; }
    [Id(2)] public int Handler3Count { get; init; }
    [Id(3)] public int SpecificCount { get; init; }
    [Id(4)] public int PrefixCount { get; init; }
    [Id(5)] public int HandlerACount { get; init; }
    [Id(6)] public int HandlerBCount { get; init; }
}

// ============================================================================
// Test Grain Interfaces
// ============================================================================

public interface ITestSenderGrain : IGrainWithGuidKey
{
    Task SendMessage(GrainId targetGrainId, string routeKey, TestMessage message);
}

public interface IMultiHandlerGrain : IGrainWithGuidKey
{
    Task<HandlerCounts> GetHandlerCounts();
}

public interface ISpecificBeforePrefixGrain : IGrainWithGuidKey
{
    Task<HandlerCounts> GetHandlerCounts();
}

public interface IOrderedHandlerGrainA : IGrainWithGuidKey
{
    Task<HandlerCounts> GetHandlerCounts();
}

public interface IOrderedHandlerGrainB : IGrainWithGuidKey
{
    Task<HandlerCounts> GetHandlerCounts();
}

// ============================================================================
// Test Grain Implementations
// ============================================================================

/// <summary>
/// Sender grain for test messages.
/// </summary>
public class TestSenderGrain(IDurableOutbox outbox) : DurableGrain, ITestSenderGrain
{
    private readonly IDurableOutbox _outbox = outbox;

    public async Task SendMessage(GrainId targetGrainId, string routeKey, TestMessage message)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(targetGrainId, routeKey)
            .WithBody(message)
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }
}

/// <summary>
/// Test grain with multiple prefix handlers for the same route pattern.
/// </summary>
public class MultiHandlerGrain(IDurableInbox inbox) : DurableGrain, IMultiHandlerGrain
{
    private int _handler1Count;
    private int _handler2Count;
    private int _handler3Count;
    private readonly IDurableInbox _inbox = inbox;

    public Task<HandlerCounts> GetHandlerCounts() => Task.FromResult(new HandlerCounts
    {
        Handler1Count = _handler1Count,
        Handler2Count = _handler2Count,
        Handler3Count = _handler3Count
    });

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register three handlers that all match "test/" prefix
        // First one should win
        _inbox.RegisterHandler(new Handler1(this));
        _inbox.RegisterHandler(new Handler2(this));
        _inbox.RegisterHandler(new Handler3(this));

        return base.OnActivateAsync(cancellationToken);
    }

    private class Handler1 : RoutePrefixHandler
    {
        private readonly MultiHandlerGrain _grain;

        public Handler1(MultiHandlerGrain grain) : base("test/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._handler1Count++;
            await _grain.WriteStateAsync();
        }
    }

    private class Handler2 : RoutePrefixHandler
    {
        private readonly MultiHandlerGrain _grain;

        public Handler2(MultiHandlerGrain grain) : base("test/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._handler2Count++;
            await _grain.WriteStateAsync();
        }
    }

    private class Handler3 : RoutePrefixHandler
    {
        private readonly MultiHandlerGrain _grain;

        public Handler3(MultiHandlerGrain grain) : base("test/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._handler3Count++;
            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Test grain with specific route handler registered before prefix handler.
/// </summary>
public class SpecificBeforePrefixGrain(IDurableInbox inbox) : DurableGrain, ISpecificBeforePrefixGrain
{
    private int _specificCount;
    private int _prefixCount;
    private readonly IDurableInbox _inbox = inbox;

    public Task<HandlerCounts> GetHandlerCounts() => Task.FromResult(new HandlerCounts
    {
        SpecificCount = _specificCount,
        PrefixCount = _prefixCount
    });

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register specific handler first
        _inbox.RegisterHandler(new SpecificHandler(this));

        // Register prefix handler second
        _inbox.RegisterHandler(new PrefixHandler(this));

        return base.OnActivateAsync(cancellationToken);
    }

    private class SpecificHandler : RouteKeyHandler
    {
        private readonly SpecificBeforePrefixGrain _grain;

        public SpecificHandler(SpecificBeforePrefixGrain grain) : base("api/v2/special")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._specificCount++;
            await _grain.WriteStateAsync();
        }
    }

    private class PrefixHandler : RoutePrefixHandler
    {
        private readonly SpecificBeforePrefixGrain _grain;

        public PrefixHandler(SpecificBeforePrefixGrain grain) : base("api/v2/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._prefixCount++;
            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Test grain that registers handler A before handler B.
/// </summary>
public class OrderedHandlerGrainA(IDurableInbox inbox) : DurableGrain, IOrderedHandlerGrainA
{
    private int _handlerACount;
    private int _handlerBCount;
    private readonly IDurableInbox _inbox = inbox;

    public Task<HandlerCounts> GetHandlerCounts() => Task.FromResult(new HandlerCounts
    {
        HandlerACount = _handlerACount,
        HandlerBCount = _handlerBCount
    });

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register handler A first (should win)
        _inbox.RegisterHandler(new HandlerA(this));
        _inbox.RegisterHandler(new HandlerB(this));

        return base.OnActivateAsync(cancellationToken);
    }

    private class HandlerA : RoutePrefixHandler
    {
        private readonly OrderedHandlerGrainA _grain;

        public HandlerA(OrderedHandlerGrainA grain) : base("order/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._handlerACount++;
            await _grain.WriteStateAsync();
        }
    }

    private class HandlerB : RoutePrefixHandler
    {
        private readonly OrderedHandlerGrainA _grain;

        public HandlerB(OrderedHandlerGrainA grain) : base("order/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._handlerBCount++;
            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Test grain that registers handler B before handler A (opposite order).
/// </summary>
public class OrderedHandlerGrainB(IDurableInbox inbox) : DurableGrain, IOrderedHandlerGrainB
{
    private int _handlerACount;
    private int _handlerBCount;
    private readonly IDurableInbox _inbox = inbox;

    public Task<HandlerCounts> GetHandlerCounts() => Task.FromResult(new HandlerCounts
    {
        HandlerACount = _handlerACount,
        HandlerBCount = _handlerBCount
    });

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register handler B first (should win)
        _inbox.RegisterHandler(new HandlerB(this));
        _inbox.RegisterHandler(new HandlerA(this));

        return base.OnActivateAsync(cancellationToken);
    }

    private class HandlerA : RoutePrefixHandler
    {
        private readonly OrderedHandlerGrainB _grain;

        public HandlerA(OrderedHandlerGrainB grain) : base("order/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._handlerACount++;
            await _grain.WriteStateAsync();
        }
    }

    private class HandlerB : RoutePrefixHandler
    {
        private readonly OrderedHandlerGrainB _grain;

        public HandlerB(OrderedHandlerGrainB grain) : base("order/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._handlerBCount++;
            await _grain.WriteStateAsync();
        }
    }
}
