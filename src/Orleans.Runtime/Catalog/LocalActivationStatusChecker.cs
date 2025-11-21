#nullable enable

namespace Orleans.Runtime;

internal sealed class LocalActivationStatusChecker(ActivationDirectory activationDirectory) : ILocalActivationStatusChecker
{
    public bool IsLocallyActivated(GrainId grainId) => activationDirectory.FindTarget(grainId) is { } activation && (activation is not ActivationData activationData || activationData.IsValid);
}
