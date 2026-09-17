using Orleans.Metadata;

namespace Orleans.Journaling;

internal sealed class JournalingGrainLifecycle : IConfigureGrainTypeComponents
{
    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        shared.AddActivationSetup(static context =>
        {
            // Later activation-setup actions can still resolve their journaled state.
            context.ObservableLifecycle.Subscribe<JournalingGrainLifecycle>(GrainLifecycleStage.First, _ =>
            {
                context.SetComponent(EnrollmentClosed.Instance);
                return Task.CompletedTask;
            });
        });
    }

    internal static void ThrowIfEnrollmentClosed(IGrainContext context)
    {
        if (context.GetComponent<EnrollmentClosed>() is not null)
        {
            throw new InvalidOperationException(
                "The durable state manager must be resolved during grain construction or setup before journaling lifecycle enrollment completes.");
        }
    }

    private sealed class EnrollmentClosed
    {
        public static readonly EnrollmentClosed Instance = new();
    }
}
