using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Invocation;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public sealed class ActivationStartupTestFixture : BaseTestClusterFixture
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    public InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)HostedCluster.Primary!;

    public IServiceProvider Services => PrimarySilo.SiloHost.Services;

    public ActivationStartupTestHooks Hooks => Services.GetRequiredService<ActivationStartupTestHooks>();

    internal ActivationDirectory ActivationDirectory => Services.GetRequiredService<ActivationDirectory>();

    public (GrainId GrainId, ActivationStartupScenario Scenario) CreateScenario(
        ActivationStartupCompletion completion,
        ActivationStartupDisposal disposal)
    {
        var grainType = Services.GetRequiredService<GrainTypeResolver>().GetGrainType(typeof(ActivationStartupTestGrain));
        var grainId = GrainId.Create(grainType, Guid.NewGuid().ToString("N"));
        return (grainId, Hooks.CreateScenario(grainId, completion, disposal));
    }

    internal ActivationData StartActivation(GrainId grainId, string? requestContextValue = null)
    {
        Dictionary<string, object>? requestContext = requestContextValue is null
            ? null
            : new() { [ActivationStartupTestHooks.RequestContextKey] = requestContextValue };
        return Assert.IsType<ActivationData>(
            Services.GetRequiredService<Catalog>().GetOrCreateActivation(grainId, requestContext, rehydrationContext: null));
    }

    internal (Message Message, ActivationStartupRequest Request) CreateRequest(
        ActivationData context,
        ActivationStartupScenario scenario,
        string payload,
        string requestContextValue,
        bool recordResponse,
        InvokeMethodOptions invokeMethodOptions = InvokeMethodOptions.OneWay)
    {
        var request = new ActivationStartupRequest(scenario, payload, recordResponse);
        var message = Services.GetRequiredService<MessageFactory>().CreateMessage(request, invokeMethodOptions);
        message.SetInfiniteTimeToLive();
        message.RequestContextData = new()
        {
            [ActivationStartupTestHooks.RequestContextKey] = requestContextValue,
        };
        message.SendingGrain = GrainId.Create("activation-startup-sender", Guid.NewGuid().ToString("N"));
        message.SendingSilo = PrimarySilo.SiloAddress;
        message.TargetGrain = context.GrainId;
        message.TargetSilo = PrimarySilo.SiloAddress;
        return (message, request);
    }

    public IDisposable ObserveLifecycle() =>
        GrainLifecycleEvents.AllEvents.Subscribe(new LifecycleObserver(Hooks));

    public void RemoveScenario(GrainId grainId) => Hooks.RemoveScenario(grainId);

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder.Services.AddSingleton<ActivationStartupTestHooks>();
            hostBuilder.Services.AddScoped<ActivationStartupScopedResource>();
            hostBuilder.Services.AddSingleton<IConfigureGrainTypeComponents, ActivationStartupTestActivator>();
        }
    }

    private sealed class ActivationStartupTestActivator(
        GrainClassMap grainClassMap,
        ActivationStartupTestHooks hooks) : IGrainActivator, IConfigureGrainTypeComponents
    {
        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
        {
            if (grainClassMap.TryGetGrainClass(grainType, out var grainClass)
                && grainClass == typeof(ActivationStartupTestGrain))
            {
                shared.SetComponent<IGrainActivator>(this);
            }
        }

        public object CreateInstance(IGrainContext context)
        {
            var scenario = hooks.GetRequiredScenario(context.GrainId);
            scenario.ObserveCreate(context);
            context.ActivationServices.GetRequiredService<ActivationStartupScopedResource>().Attach(context);
            return new ActivationStartupTestGrain(context, hooks);
        }

        public async ValueTask DisposeInstance(IGrainContext context, object instance)
        {
            var scenario = hooks.GetRequiredScenario(context.GrainId);
            scenario.ObserveDisposeStarted(context);
            if (scenario.Disposal is ActivationStartupDisposal.Asynchronous)
            {
                await scenario.DisposalRelease;
            }

            scenario.ObserveDisposeCompleted(context);
        }
    }

    private sealed class LifecycleObserver(ActivationStartupTestHooks hooks)
        : IObserver<GrainLifecycleEvents.LifecycleEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(GrainLifecycleEvents.LifecycleEvent value)
        {
            if (!hooks.TryGetScenario(value.GrainContext.GrainId, out var scenario))
            {
                return;
            }

            var name = value switch
            {
                GrainLifecycleEvents.Created => "Created",
                GrainLifecycleEvents.Activated => "Activated",
                GrainLifecycleEvents.Deactivating => "Deactivating",
                GrainLifecycleEvents.Deactivated => "Deactivated",
                _ => null,
            };
            if (name is not null)
            {
                scenario!.Record(name, value.GrainContext);
            }
        }
    }
}

internal sealed class ActivationStartupRequest(
    ActivationStartupScenario scenario,
    string payload,
    bool recordResponse) : IInvokable
{
    private static readonly MethodInfo Method =
        typeof(IActivationStartupTestGrain).GetMethod(nameof(IActivationStartupTestGrain.Invoke))!;

    private IActivationStartupTestGrain? _target;
    private string? _result;

    public string? Result => Volatile.Read(ref _result);

    public object? GetTarget() => _target;

    public void SetTarget(ITargetHolder holder)
    {
        _target = (IActivationStartupTestGrain)holder.GetTarget()!;
    }

    public async ValueTask<Response> Invoke()
    {
        var result = await _target!.Invoke(payload);
        Volatile.Write(ref _result, result);
        if (recordResponse)
        {
            scenario.Record("Response", ((IGrainBase)_target).GrainContext);
        }

        return Response.FromResult(result);
    }

    public int GetArgumentCount() => 1;

    public object? GetArgument(int index) =>
        index == 0 ? payload : throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object value) =>
        throw new NotSupportedException("The activation startup request is immutable.");

    public string GetMethodName() => nameof(IActivationStartupTestGrain.Invoke);

    public string GetInterfaceName() => typeof(IActivationStartupTestGrain).FullName!;

    public string GetActivityName() => $"{GetInterfaceName()}/{GetMethodName()}";

    public MethodInfo GetMethod() => Method;

    public Type GetInterfaceType() => typeof(IActivationStartupTestGrain);

    public void Dispose()
    {
    }
}
