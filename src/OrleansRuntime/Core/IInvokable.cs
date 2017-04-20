using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Common internal interface for SystemTarget and ActivationData.
    /// </summary>
    internal interface IInvokable
    {
        IGrainMethodInvoker GetInvoker(GrainTypeManager typeManager, int interfaceId, string genericGrainType = null);
    }
}