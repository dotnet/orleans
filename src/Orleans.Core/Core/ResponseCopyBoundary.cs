using System;
using System.Collections.Generic;
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

internal struct ResponseOwnership : IDisposable
{
    private Response? _current;
    private List<Response>? _others;

    internal Response? Value
    {
        readonly get => _current;
        set
        {
            if (ReferenceEquals(_current, value)) return;
            if (_current is { } previous) (_others ??= []).Add(previous);
            if (value is not null && _others is { } others)
            {
                for (var i = others.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(value, others[i])) others.RemoveAt(i);
                }
            }

            _current = value;
        }
    }

    internal Response Take()
    {
        var result = _current!;
        _current = null;
        return result;
    }

    public void Dispose()
    {
        var current = _current;
        _current = null;
        var others = _others;
        _others = null;
        try
        {
            current?.Dispose();
        }
        finally
        {
            if (others is not null)
                foreach (var response in others) response.Dispose();
        }
    }
}
