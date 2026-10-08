using System;
using System.Threading.Tasks;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// Provides the provider-owned serialization services used to isolate invocation results.
/// </summary>
/// <remarks>
/// A context can be reused across invocations using the same serialization provider.
/// Successful results are isolated before incoming filters resume. Exceptions retain their original identity until delivery.
/// </remarks>
public sealed class InvocationContext
{
    /// <summary>
    /// Initializes an invocation context with its serialization services.
    /// </summary>
    /// <param name="codecProvider">The provider used to resolve concrete result services.</param>
    /// <param name="copyContextPool">The pool used to copy mutable result graphs.</param>
    /// <param name="responseCopier">The selected copier used by compatibility invocations.</param>
    public InvocationContext(ICodecProvider codecProvider, CopyContextPool copyContextPool, DeepCopier<Response> responseCopier)
    {
        CodecProvider = codecProvider ?? throw new ArgumentNullException(nameof(codecProvider));
        CopyContextPool = copyContextPool ?? throw new ArgumentNullException(nameof(copyContextPool));
        ResponseCopier = responseCopier ?? throw new ArgumentNullException(nameof(responseCopier));
    }

    /// <summary>
    /// Gets the provider used to resolve concrete result services.
    /// </summary>
    public ICodecProvider CodecProvider { get; }

    /// <summary>
    /// Gets the pool used to copy mutable result graphs.
    /// </summary>
    public CopyContextPool CopyContextPool { get; }

    /// <summary>
    /// Gets the selected copier used by compatibility invocations.
    /// </summary>
    public DeepCopier<Response> ResponseCopier { get; }

    /// <summary>
    /// Invokes a request's parameterless entry point and isolates its successful result using the selected response copier.
    /// </summary>
    /// <param name="request">The request using compatibility response implementations.</param>
    /// <returns>An owned isolated response, or an exception response representing an invocation or copying failure.</returns>
    /// <remarks>Generated invocations use this path when the provider selects custom result or response services.</remarks>
    public async ValueTask<Response> InvokeCompatibility(IInvokable request)
    {
        try
        {
            return CopyResult(await request.Invoke());
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }

    internal async ValueTask<Response> Invoke(IInvokable request)
        => CopyResult(await request.Invoke());

    private Response CopyResult(Response response)
    {
        Response? ownedResponse = response;
        try
        {
            if (response.Exception is not null)
            {
                ownedResponse = null;
                return response;
            }

            ownedResponse = null;
            return OrleansGeneratedCodeHelper.CopyResponseAndDispose(response, ResponseCopier);
        }
        finally
        {
            ownedResponse?.Dispose();
        }
    }
}
