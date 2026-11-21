using Orleans.Runtime;

namespace Orleans.Dissemination.PerformanceHarness;

[Alias("dissemination-performance-control-v1")]
internal interface IControlTarget : ISystemTarget
{
    [Alias("echo-v1")]
    Task<int> Echo(CancellationToken cancellationToken);
}

internal sealed class ControlTarget : SystemTarget, IControlTarget
{
    public static readonly GrainType TargetType = SystemTargetGrainId.CreateGrainType("test.dissemination-performance");

    public ControlTarget(SystemTargetShared shared) : base(TargetType, shared) =>
        shared.ActivationDirectory.RecordNewTarget(this);

    public Task<int> Echo(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Environment.ProcessId);
    }
}
