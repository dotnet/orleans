using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Binds the durable task runtime to activations of the grain types which use messaging.
/// </summary>
/// <remarks>
/// Applies to grain classes deriving from <see cref="DurableGrain"/> or implementing <see cref="IMessagingGrain"/>, the
/// same grain classes for which messaging installs an inbox and outbox. Activation setup runs before journal recovery,
/// so the runtime's journal hook, inbox sink and handler, and outbox message source are registered before recovered
/// state or recovered messages are processed.
/// </remarks>
internal sealed class DurableTaskGrainTypeConfigurator(GrainClassMap grainClasses) : IConfigureGrainTypeComponents
{
    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (!grainClasses.TryGetGrainClass(grainType, out var grainClass)
            || (!typeof(DurableGrain).IsAssignableFrom(grainClass) && !typeof(IMessagingGrain).IsAssignableFrom(grainClass)))
        {
            return;
        }

        shared.AddActivationSetup(static context =>
        {
            var services = context.ActivationServices;
            var runtime = services.GetRequiredService<DurableTaskGrainRuntime>();
            services.GetRequiredService<IJournaledStateManager>()
                .RegisterStateMachine(DurableTaskGrainRuntime.StateMachineName, runtime);
            var handler = services.GetRequiredService<DurableTaskMessageHandler>();
            services.GetRequiredService<IInbox>()
                .Register((IInboxMessageSink)handler)
                .Register((IInboxHandler)handler);
            services.GetRequiredService<IOutbox>().Register(runtime);
            context.ObservableLifecycle.Subscribe(
                nameof(DurableTaskGrainRuntime),
                GrainLifecycleStage.Activate,
                runtime.OnActivateAsync,
                runtime.StopAsync);
            runtime.InitializeForActivation();
        });
    }
}
