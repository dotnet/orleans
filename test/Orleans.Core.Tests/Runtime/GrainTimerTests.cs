using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Timers;
using Xunit;

namespace NonSilo.Tests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GrainTimerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateOneShot_QueuesActivationMessageWithoutPhysicalTimer(bool interleave)
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        IGrainTimer timer;
        RequestContext.Set("timer-parent", "request-data");
        try
        {
            timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan) { Interleave = interleave, KeepAlive = true });
        }
        finally
        {
            RequestContext.Remove("timer-parent");
        }
        using var lifetime = timer;
        var message = Assert.Single(fixture.Messages);
        Assert.Equal(0, calls);
        Assert.Equal(0, fixture.Time.TimerCreations);
        Assert.Equal(interleave, message.IsAlwaysInterleave);
        Assert.True(message.IsKeepAlive);
        Assert.True(message.IsLocalOnly);
        Assert.Null(message.RequestContextData);
        await fixture.InvokeAsync(message);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    [Fact]
    public async Task ImmediateTimer_ChangedInsideCallbackCreatesDelayedTimerAndRespectsPeriod()
    {
        using var fixture = new TimerFixture();
        var calls = 0;
        IGrainTimer? timer = null;
        timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                timer!.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            }
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        using (timer)
        {
            await fixture.InvokeAsync(Assert.Single(fixture.Messages));
            Assert.Equal(1, fixture.Time.TimerCreations);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Single(fixture.Messages);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(2, fixture.Messages.Count);
            await fixture.InvokeAsync(fixture.Messages[1]);
            fixture.Time.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(3, fixture.Messages.Count);
            await fixture.InvokeAsync(fixture.Messages[2]);
            Assert.Equal(3, calls);
            Assert.Equal(1, fixture.Time.TimerCreations);
        }
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(3, fixture.Messages.Count);
    }

    [Fact]
    public async Task DisposedImmediateTimer_QueuedInvocationHasCanceledTokenAndCreatesNoTimer()
    {
        using var fixture = new TimerFixture();
        var canceled = false;
        var timer = fixture.Registry.RegisterGrainTimer(fixture.Grain, (_, token) =>
        {
            canceled = token.IsCancellationRequested;
            return Task.CompletedTask;
        }, 0, new(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        timer.Dispose();
        timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        await fixture.InvokeAsync(Assert.Single(fixture.Messages));
        Assert.True(canceled);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Time.TimerCreations);
    }

    private sealed class TimerFixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        public TrackingTimeProvider Time { get; } = new();
        public IGrainContext Grain { get; } = Substitute.For<IGrainContext>();
        public List<Message> Messages { get; } = [];
        public TimerRegistry Registry { get; }

        public TimerFixture()
        {
            Grain.GrainId.Returns(GrainId.Create("timer-test", "one"));
            Grain.When(context => context.ReceiveMessage(Arg.Any<object>())).Do(call => Messages.Add((Message)call[0]));
            var details = Substitute.For<ILocalSiloDetails>();
            details.SiloAddress.Returns(SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1));
            var factory = new MessageFactory(_services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, null!);
            Registry = new(NullLoggerFactory.Instance, Time, factory, details);
        }

        public async Task InvokeAsync(Message message)
        {
            var invokable = Assert.IsAssignableFrom<IInvokable>(message.BodyObject);
            invokable.SetTarget(Grain);
            using var response = await invokable.Invoke();
            Assert.Null(response.Exception);
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class TrackingTimeProvider : FakeTimeProvider
    {
        public int TimerCreations { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCreations++;
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
