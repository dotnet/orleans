using Orleans;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class LifecycleExtensionsTests
{
    [Fact]
    public void Subscribe_NullObservable_ThrowsWithExactParameterName()
    {
        ILifecycleObservable observable = null!;
        var observer = new TestObserver();

        var exception = Assert.Throws<ArgumentNullException>(() => observable.Subscribe(1, observer));
        Assert.Equal("observable", exception.ParamName);

        exception = Assert.Throws<ArgumentNullException>(() => observable.Subscribe<TestObserver>(1, observer));
        Assert.Equal("observable", exception.ParamName);
    }

    [Fact]
    public void Subscribe_NullObserver_ThrowsBeforeObservableCall()
    {
        var observable = new RecordingLifecycleObservable();

        var exception = Assert.Throws<ArgumentNullException>(() => observable.Subscribe(1, null!));

        Assert.Equal("observer", exception.ParamName);
        Assert.Equal(0, observable.SubscribeCallCount);

        exception = Assert.Throws<ArgumentNullException>(
            () => observable.Subscribe<TestObserver>(1, (ILifecycleObserver)null!));
        Assert.Equal("observer", exception.ParamName);
        Assert.Equal(0, observable.SubscribeCallCount);
    }

    [Fact]
    public void Subscribe_ValidObserver_PreservesNameStageIdentityAndRegistration()
    {
        var registration = new TestRegistration();
        var observable = new RecordingLifecycleObservable(registration);
        var observer = new TestObserver();

        var result = observable.Subscribe<TestObserver>(17, observer);

        Assert.Same(registration, result);
        Assert.Equal(typeof(TestObserver).FullName, observable.ObserverName);
        Assert.Equal(17, observable.Stage);
        Assert.Same(observer, observable.Observer);
    }

    private sealed class RecordingLifecycleObservable(IDisposable? registration = null) : ILifecycleObservable
    {
        public int SubscribeCallCount { get; private set; }

        public string? ObserverName { get; private set; }

        public int Stage { get; private set; }

        public ILifecycleObserver? Observer { get; private set; }

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            SubscribeCallCount++;
            ObserverName = observerName;
            Stage = stage;
            Observer = observer;
            return registration ?? new TestRegistration();
        }
    }

    private sealed class TestObserver : ILifecycleObserver
    {
        public Task OnStart(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnStop(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestRegistration : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
