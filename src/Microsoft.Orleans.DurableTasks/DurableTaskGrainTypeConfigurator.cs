using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
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
/// so the runtime's journal hook, inbox handler, and resume job handler are registered before recovered state or
/// recovered messages are processed.
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
            services.GetRequiredService<IDurableJobHandlerRegistry>().Register(runtime);
            services.GetRequiredService<IInbox>().Register(services.GetRequiredService<DurableTaskMessageHandler>());
            context.ObservableLifecycle.Subscribe(
                nameof(DurableTaskGrainRuntime),
                GrainLifecycleStage.Activate,
                runtime.OnActivateAsync,
                runtime.StopAsync);
            runtime.InitializeForActivation();
        });
    }
}
