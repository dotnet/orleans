using NSubstitute;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Orleans.Timers;
using Xunit;

namespace NonSilo.Tests.Runtime;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class GrainBaseExtensionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RequestOverloadsRejectNullGrainBeforeInvokingCallback(int shape)
    {
        var callbacks = 0;

        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = Submit(null!, shape, _ => callbacks++, TestContext.Current.CancellationToken);
        });

        Assert.Equal("grain", exception.ParamName);
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RequestOverloadsRetainActivationAdmissionAndCallbackResults(int shape)
    {
        var context = Substitute.For<IGrainContext>();
        var grain = Substitute.For<IGrainBase>();
        grain.GrainContext.Returns(context);
        ILocalRequest request = null!;
        LocalRequestOptions options = default;
        context.When(value => value.ReceiveLocalRequest(Arg.Any<ILocalRequest>(), Arg.Any<LocalRequestOptions>()))
            .Do(call =>
            {
                request = call.Arg<ILocalRequest>();
                options = call.Arg<LocalRequestOptions>();
            });
        var callbacks = 0;
        using var cancellation = new CancellationTokenSource();

        var operation = Submit(grain, shape, token =>
        {
            Assert.Equal(cancellation.Token, token);
            callbacks++;
        }, cancellation.Token);
        using var ownedRequest = request;

        Assert.Equal(0, callbacks);
        Assert.False(operation.IsCompleted);
        Assert.True(options.Interleave);
        Assert.False(options.KeepAlive);
        var response = await request.Invoke();
        request.Complete(response);
        await operation.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, callbacks);
        if (shape >= 4)
        {
            Assert.Equal(42, await Assert.IsAssignableFrom<Task<int>>(operation));
        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LifecycleHelpersRejectNullGrain(bool migrate)
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            IGrainBase grain = null!;
            if (migrate) grain.MigrateOnIdle();
            else grain.DeactivateOnIdle();
        });

        Assert.Equal("grain", exception.ParamName);
    }

    [Fact]
    public void LifecycleHelpersForwardToActivation()
    {
        var context = Substitute.For<IGrainContext>();
        var grain = Substitute.For<IGrainBase>();
        grain.GrainContext.Returns(context);

        grain.DeactivateOnIdle();
        grain.MigrateOnIdle();

        context.Received(1).Deactivate(Arg.Is<DeactivationReason>(
            reason => reason.ReasonCode == DeactivationReasonCode.ApplicationRequested), CancellationToken.None);
        context.Received(1).Migrate(Arg.Any<Dictionary<string, object>?>(), CancellationToken.None);
    }

    [Fact]
    public void TimerRegistrationRejectsNullGrain()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ((IGrainBase)null!).RegisterGrainTimer(static (_, _) => Task.CompletedTask, 42,
                new GrainTimerCreationOptions { DueTime = Timeout.InfiniteTimeSpan, Period = Timeout.InfiniteTimeSpan }));

        Assert.Equal("grain", exception.ParamName);
    }

    private static Task Submit(IGrainBase grain, int shape, Action<CancellationToken> callback, CancellationToken cancellationToken)
    {
        var options = new LocalRequestOptions { Interleave = true, KeepAlive = false };
        return shape switch
        {
            0 => grain.RunRequestAsync(token => { callback(token); return Task.CompletedTask; }, options, cancellationToken),
            1 => grain.RunRequestAsync((state, token) => { state(token); return Task.CompletedTask; }, callback, options, cancellationToken),
            2 => grain.RunRequestAsync(callback, options, cancellationToken),
            3 => grain.RunRequestAsync(static (state, token) => state(token), callback, options, cancellationToken),
            4 => grain.RunRequestAsync(token => { callback(token); return Task.FromResult(42); }, options, cancellationToken),
            5 => grain.RunRequestAsync((state, token) => { state(token); return Task.FromResult(42); }, callback, options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }
}
