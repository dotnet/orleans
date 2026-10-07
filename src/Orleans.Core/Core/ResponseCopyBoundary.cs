using System;
using System.Threading.Tasks;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;

namespace Orleans.Runtime;

internal static class ResponseCopyBoundary
{
    internal static ValueTask<Response> InvokeAndCopy(
        IInvokable request,
        ICodecProvider codecProvider,
        CopyContextPool copyContexts,
        DeepCopier<Response> responseCopier)
        => request is IResponseInvokable direct
            ? direct.InvokeAndCopy(codecProvider, copyContexts, responseCopier)
            : InvokeLegacy(request, responseCopier);

    private static async ValueTask<Response> InvokeLegacy(IInvokable request, DeepCopier<Response> responseCopier)
        => CopyAndDispose(await request.Invoke(), responseCopier);

    internal static Response CopyAndDispose(Response response, DeepCopier<Response> copier)
        => OrleansGeneratedCodeHelper.CopyResponseAndDispose(response, copier);

    internal static Response CopyAndDispose(Response response, DeepCopier copier)
        => OrleansGeneratedCodeHelper.CopyResponseAndDispose(response, copier);
}
