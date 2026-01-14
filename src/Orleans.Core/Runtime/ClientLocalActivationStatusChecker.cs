#nullable enable
namespace Orleans.Runtime;

internal sealed class ClientLocalActivationStatusChecker : ILocalActivationStatusChecker
{
    public bool IsLocallyActivated(GrainId grainId) => false;
}
