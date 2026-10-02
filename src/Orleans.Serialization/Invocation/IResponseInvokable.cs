using System.Threading.Tasks;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// Invokes a source-known method and creates a response whose result satisfies the runtime copy boundary.
/// </summary>
public interface IResponseInvokable
{
    /// <summary>
    /// Invokes the method and isolates its successful result before returning to incoming filters.
    /// </summary>
    /// <param name="codecProvider">The invocation's serialization provider.</param>
    /// <param name="copyContextPool">The invocation's copy-context pool.</param>
    /// <param name="responseCopier">The compatibility copier for custom response implementations.</param>
    /// <returns>The isolated response.</returns>
    ValueTask<Response> InvokeAndCopy(ICodecProvider codecProvider, CopyContextPool copyContextPool, DeepCopier<Response> responseCopier);
}
