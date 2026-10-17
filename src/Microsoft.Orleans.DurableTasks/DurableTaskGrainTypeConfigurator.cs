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
/// <para>
/// Applies to grain classes deriving from <see cref="DurableGrain"/> or implementing <see cref="IMessagingGrain"/>, the
/// same grain classes for which messaging installs an inbox and outbox. Activation setup runs before journal recovery,
/// so the runtime's journal hook, inbox sink, handler, and work source, and outbox message source are registered before
/// recovered state or recovered messages are processed.
/// </para>
/// <para>
/// The runtime's lifecycle stage follows <see cref="GrainLifecycleStage.Activate"/>, at which messaging starts the
/// grain's inbox and outbox, which in turn follows <see cref="GrainLifecycleStage.SetupState"/>, at which the journal
/// recovers. The runtime therefore recovers task state once the journal, inbox, and outbox are ready, and stops before
/// messaging and the journal stop, so the executions it drains can still commit and send. Recovered executions start once
/// the grain's <c>OnActivateAsync</c> has completed, and running executions are signaled to abort before its
/// <c>OnDeactivateAsync</c> runs; see <see cref="DurableTaskActivationObserver"/>.
/// </para>
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
                .Register((IInboxHandler)handler)
                .Register((IInboxWorkSource)runtime);
            services.GetRequiredService<IOutbox>().Register(runtime);
            context.ObservableLifecycle.Subscribe(
                nameof(DurableTaskGrainRuntime),
                GrainLifecycleStage.Activate + 1,
                runtime.RecoverAsync,
                runtime.StopAsync);
            runtime.InitializeForActivation();
        });
    }
}
