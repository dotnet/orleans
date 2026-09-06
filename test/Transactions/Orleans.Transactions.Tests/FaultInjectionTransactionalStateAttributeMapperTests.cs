using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class FaultInjectionTransactionalStateAttributeMapperTests
{
    [Fact]
    public void GetFactory_NullParameter_ThrowsWithParameterParamNameBeforeActivation()
    {
        var mapper = new FaultInjectionTransactionalStateAttributeMapper();

        var exception = Assert.Throws<ArgumentNullException>(
            () => mapper.GetFactory(
                parameter: null!,
                attribute: null!));

        Assert.Equal("parameter", exception.ParamName);
    }

    [Fact]
    public void GetFactory_ValidAttribute_UsesParameterStateTypeAndOriginalConfiguration()
    {
        var expectedState = new RecordingTransactionalState();
        var stateFactory = new RecordingTransactionalStateFactory(expectedState);
        using var services = new ServiceCollection()
            .AddSingleton<IFaultInjectionTransactionalStateFactory>(stateFactory)
            .BuildServiceProvider();
        var context = new TestGrainContext(services);
        var parameter = typeof(FaultInjectionTransactionalStateAttributeMapperTests)
            .GetMethod(
                nameof(AttributeTarget),
                BindingFlags.Static | BindingFlags.NonPublic)!
            .GetParameters()
            .Single();
        var attribute = Assert.IsType<FaultInjectionTransactionalStateAttribute>(
            parameter.GetCustomAttribute<FaultInjectionTransactionalStateAttribute>());
        var mapper = new FaultInjectionTransactionalStateAttributeMapper();

        var factory = mapper.GetFactory(parameter, attribute);
        var result = factory(context);

        Assert.Same(expectedState, result);
        Assert.Equal(1, stateFactory.CreateCallCount);
        Assert.Equal(typeof(TestState), stateFactory.CreatedStateType);
        Assert.Same(attribute, stateFactory.Configuration);
        Assert.Equal("account", stateFactory.Configuration!.StateName);
        Assert.Equal("fault-store", stateFactory.Configuration.StorageName);
    }

    private static void AttributeTarget(
        [FaultInjectionTransactionalState("account", "fault-store")]
        IFaultInjectionTransactionalState<TestState> state)
    {
    }

    private sealed class RecordingTransactionalStateFactory(
        RecordingTransactionalState state)
        : IFaultInjectionTransactionalStateFactory
    {
        public int CreateCallCount { get; private set; }

        public Type? CreatedStateType { get; private set; }

        public IFaultInjectionTransactionalStateConfiguration? Configuration { get; private set; }

        public IFaultInjectionTransactionalState<TState> Create<TState>(
            IFaultInjectionTransactionalStateConfiguration config)
            where TState : class, new()
        {
            CreateCallCount++;
            CreatedStateType = typeof(TState);
            Configuration = config;
            return (IFaultInjectionTransactionalState<TState>)(object)state;
        }
    }

    private sealed class RecordingTransactionalState
        : IFaultInjectionTransactionalState<TestState>
    {
        public FaultInjectionControl FaultInjectionControl { get; set; } = new();

        public Task<TResult> PerformRead<TResult>(Func<TestState, TResult> readFunction) =>
            Task.FromResult(readFunction(new TestState()));

        public Task<TResult> PerformUpdate<TResult>(Func<TestState, TResult> updateFunction) =>
            Task.FromResult(updateFunction(new TestState()));
    }

    private sealed class TestGrainContext(IServiceProvider activationServices) : IGrainContext
    {
        public GrainReference GrainReference => throw new NotSupportedException();

        public GrainId GrainId => throw new NotSupportedException();

        public object? GrainInstance => null;

        public ActivationId ActivationId => throw new NotSupportedException();

        public GrainAddress Address => throw new NotSupportedException();

        public IServiceProvider ActivationServices => activationServices;

        public IGrainLifecycle ObservableLifecycle => throw new NotSupportedException();

        public IWorkItemScheduler Scheduler => throw new NotSupportedException();

        public Task Deactivated => Task.CompletedTask;

        public void Activate(
            Dictionary<string, object>? requestContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Deactivate(
            DeactivationReason deactivationReason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);

        public TComponent? GetComponent<TComponent>() where TComponent : class => null;

        public object? GetComponent(Type componentType) => null;

        public TTarget? GetTarget<TTarget>() where TTarget : class => null;

        public object? GetTarget() => null;

        public void Migrate(
            Dictionary<string, object>? requestContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void ReceiveMessage(object message) => throw new NotSupportedException();

        public void Rehydrate(IRehydrationContext context) => throw new NotSupportedException();

        public void SetComponent<TComponent>(TComponent? value)
            where TComponent : class =>
            throw new NotSupportedException();
    }

    private sealed class TestState
    {
        public string Value { get; set; } = string.Empty;
    }
}
