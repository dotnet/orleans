using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.PubSub;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public class StreamingBoundaryArgumentValidationTests
{
    [Fact]
    public void DefaultPredicateProviderRejectsNullPattern()
    {
        var provider = new DefaultStreamNamespacePredicateProvider();

        var exception = Assert.Throws<ArgumentNullException>(
            () => provider.TryGetPredicate(null!, out _));

        Assert.Equal("predicatePattern", exception.ParamName);
    }

    [Fact]
    public void ConstructorPredicateProviderRejectsNullPattern()
    {
        var provider = new ConstructorStreamNamespacePredicateProvider();

        var exception = Assert.Throws<ArgumentNullException>(
            () => provider.TryGetPredicate(null!, out _));

        Assert.Equal("predicatePattern", exception.ParamName);
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("namespaced-guid")]
    [InlineData("string")]
    [InlineData("namespaced-string")]
    [InlineData("long")]
    [InlineData("namespaced-long")]
    public void GetStreamOverloadsRejectNullProvider(string overload)
    {
        Action invocation = overload switch
        {
            "guid" => () => StreamProviderExtensions.GetStream<int>(null!, Guid.Empty),
            "namespaced-guid" => () => StreamProviderExtensions.GetStream<int>(null!, "orders", Guid.Empty),
            "string" => () => StreamProviderExtensions.GetStream<int>(null!, "order-1"),
            "namespaced-string" => () => StreamProviderExtensions.GetStream<int>(null!, "orders", "order-1"),
            "long" => () => StreamProviderExtensions.GetStream<int>(null!, 1L),
            "namespaced-long" => () => StreamProviderExtensions.GetStream<int>(null!, "orders", 1L),
            _ => throw new ArgumentOutOfRangeException(nameof(overload)),
        };

        var exception = Assert.Throws<ArgumentNullException>(invocation);

        Assert.Equal("streamProvider", exception.ParamName);
    }

    [Fact]
    public async Task GetStreamForwardsIdentityAndReturnedStreamPublishes()
    {
        var provider = Substitute.For<IStreamProvider>();
        var stream = Substitute.For<IAsyncStream<int>>();
        var streamId = StreamId.Create("orders", "order-1");
        provider.GetStream<int>(streamId).Returns(stream);

        var result = provider.GetStream<int>("orders", "order-1");
        await result.OnNextAsync(42);

        Assert.Same(stream, result);
        _ = provider.Received(1).GetStream<int>(streamId);
        await stream.Received(1).OnNextAsync(42, null);
    }

    [Fact]
    public void AddSubscriptionRejectsNullManagerBeforeGrainLookup()
    {
        var grainFactory = Substitute.For<IGrainFactory>();

        AssertArgumentNull(
            () => StreamSubscriptionManagerExtensions.AddSubscription<ITestGuidGrain>(
                null!,
                grainFactory,
                StreamId.Create("orders", "order-1"),
                "provider",
                Guid.Empty),
            "manager");

        Assert.Empty(grainFactory.ReceivedCalls());
    }

    [Fact]
    public void AddSubscriptionRejectsNullGrainFactoryBeforeRegistration()
    {
        var manager = Substitute.For<IStreamSubscriptionManager>();

        AssertArgumentNull(
            () => manager.AddSubscription<ITestGuidGrain>(
                null!,
                StreamId.Create("orders", "order-1"),
                "provider",
                Guid.Empty),
            "grainFactory");

        Assert.Empty(manager.ReceivedCalls());
    }

    [Fact]
    public void ObservableSubscribeEntryPointsRejectNullObservableWithoutInvokingCallbacks()
    {
        var observer = Substitute.For<IAsyncObserver<int>>();
        var token = Substitute.For<StreamSequenceToken>();
        var callbackCount = 0;
        Func<int, StreamSequenceToken?, Task> onNext = (_, _) =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Func<Exception, Task> onError = _ =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Func<Task> onCompleted = () =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };

        AssertArgumentNull(
            () => AsyncObservableExtensions.SubscribeAsync(
                null!,
                observer,
                StreamSubscriptionStartPosition.Latest),
            "obs");
        AssertArgumentNull(
            () => AsyncObservableExtensions.SubscribeAsync(null!, onNext, onError, onCompleted),
            "obs");
        AssertArgumentNull(
            () => AsyncObservableExtensions.SubscribeAsync(null!, onNext, onError, onCompleted, token),
            "obs");

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void BatchObservableSubscribeEntryPointsRejectNullObservableWithoutInvokingCallbacks()
    {
        var observer = Substitute.For<IAsyncBatchObserver<int>>();
        var callbackCount = 0;
        Func<IList<SequentialItem<int>>, Task> onNext = _ =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Func<Exception, Task> onError = _ =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Func<Task> onCompleted = () =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };

        AssertArgumentNull(
            () => AsyncBatchObservableExtensions.SubscribeAsync(
                null!,
                observer,
                StreamSubscriptionStartPosition.Latest),
            "obs");
        AssertArgumentNull(
            () => AsyncBatchObservableExtensions.SubscribeAsync(null!, onNext, onError, onCompleted),
            "obs");

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void ResumeEntryPointsRejectNullHandleWithoutInvokingCallbacks()
    {
        var callbackCount = 0;
        Func<int, StreamSequenceToken?, Task> onNext = (_, _) =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Func<IList<SequentialItem<int>>, Task> onNextBatch = _ =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Func<Exception, Task> onError = _ =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Func<Task> onCompleted = () =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };

        AssertArgumentNull(
            () => StreamSubscriptionHandleExtensions.ResumeAsync(
                null!,
                onNext,
                onError,
                onCompleted),
            "handle");
        AssertArgumentNull(
            () => StreamSubscriptionHandleExtensions.ResumeAsync(
                null!,
                onNextBatch,
                onError,
                onCompleted),
            "handle");

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task DelegateSubscribeForwardsCallbacks()
    {
        var observable = Substitute.For<IAsyncObservable<int>>();
        var handle = Substitute.For<StreamSubscriptionHandle<int>>();
        IAsyncObserver<int>? registeredObserver = null;
        observable.SubscribeAsync(Arg.Do<IAsyncObserver<int>>(value => registeredObserver = value))
            .Returns(Task.FromResult(handle));
        var receivedItem = 0;
        StreamSequenceToken? receivedToken = null;
        Exception? receivedError = null;
        var completed = false;

        var result = await observable.SubscribeAsync(
            (item, token) =>
            {
                receivedItem = item;
                receivedToken = token;
                return Task.CompletedTask;
            },
            exception =>
            {
                receivedError = exception;
                return Task.CompletedTask;
            },
            () =>
            {
                completed = true;
                return Task.CompletedTask;
            });

        var token = Substitute.For<StreamSequenceToken>();
        var error = new InvalidOperationException("test");
        Assert.NotNull(registeredObserver);
        await registeredObserver.OnNextAsync(42, token);
        await registeredObserver.OnErrorAsync(error);
        await registeredObserver.OnCompletedAsync();

        Assert.Same(handle, result);
        Assert.Equal(42, receivedItem);
        Assert.Same(token, receivedToken);
        Assert.Same(error, receivedError);
        Assert.True(completed);
    }

    [Fact]
    public async Task BatchDelegateSubscribeForwardsCallbacks()
    {
        var observable = Substitute.For<IAsyncBatchObservable<int>>();
        var handle = Substitute.For<StreamSubscriptionHandle<int>>();
        IAsyncBatchObserver<int>? registeredObserver = null;
        observable.SubscribeAsync(Arg.Do<IAsyncBatchObserver<int>>(value => registeredObserver = value))
            .Returns(Task.FromResult(handle));
        IList<SequentialItem<int>>? receivedItems = null;
        Exception? receivedError = null;
        var completed = false;

        var result = await observable.SubscribeAsync(
            items =>
            {
                receivedItems = items;
                return Task.CompletedTask;
            },
            exception =>
            {
                receivedError = exception;
                return Task.CompletedTask;
            },
            () =>
            {
                completed = true;
                return Task.CompletedTask;
            });

        var items = new List<SequentialItem<int>>
        {
            new(42, Substitute.For<StreamSequenceToken>()),
        };
        var error = new InvalidOperationException("test");
        Assert.NotNull(registeredObserver);
        await registeredObserver.OnNextAsync(items);
        await registeredObserver.OnErrorAsync(error);
        await registeredObserver.OnCompletedAsync();

        Assert.Same(handle, result);
        Assert.Same(items, receivedItems);
        Assert.Same(error, receivedError);
        Assert.True(completed);
    }

    [Fact]
    public async Task DelegateResumeForwardsCallbacksAndToken()
    {
        var handle = Substitute.For<StreamSubscriptionHandle<int>>();
        var resumedHandle = Substitute.For<StreamSubscriptionHandle<int>>();
        var token = Substitute.For<StreamSequenceToken>();
        IAsyncObserver<int>? registeredObserver = null;
        handle.ResumeAsync(
                Arg.Do<IAsyncObserver<int>>(value => registeredObserver = value),
                token)
            .Returns(Task.FromResult(resumedHandle));
        var receivedItem = 0;

        var result = await handle.ResumeAsync(
            (item, _) =>
            {
                receivedItem = item;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            token);

        Assert.NotNull(registeredObserver);
        await registeredObserver.OnNextAsync(42, token);

        Assert.Same(resumedHandle, result);
        Assert.Equal(42, receivedItem);
        await handle.Received(1).ResumeAsync(registeredObserver, token);
    }

    private static void AssertArgumentNull(Action action, string paramName)
    {
        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(paramName, exception.ParamName);
    }

    public interface ITestGuidGrain : IGrainWithGuidKey;
}
