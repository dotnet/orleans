using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

internal abstract class GrainCallInvoker(IInvokable request) : IGrainCallContext, IDisposable
{
    private Response? _response;
    private HashSet<Response>? _ownedResponses;
    private int _stage;

    public IInvokable Request { get; } = request;
    public MethodInfo InterfaceMethod => Request.GetMethod();
    public string InterfaceName => Request.GetInterfaceName();
    public string MethodName => Request.GetMethodName();
    public abstract object Grain { get; }
    public abstract GrainId? SourceId { get; }
    public abstract GrainId TargetId { get; }
    public abstract GrainInterfaceType InterfaceType { get; }
    public abstract object? Result { get; set; }
    protected abstract int FilterCount { get; }

    public Response? Response
    {
        get => _response;
        set
        {
            if (ReferenceEquals(_response, value)) return;
            if (_response is { } previous)
                (_ownedResponses ??= new(ReferenceEqualityComparer.Instance)).Add(previous);
            if (_ownedResponses is { } owned && value is not null) owned.Add(value);
            _response = value;
        }
    }

    public async Task Invoke()
    {
        var stage = _stage++;
        try
        {
            if (stage < FilterCount)
            {
                await InvokeFilter(stage);
                if (Response is null)
                    throw new InvalidOperationException($"{GetType()}.{nameof(Invoke)}() invoked a broken filter: {GetFilterName(stage)}.");
            }
            else if (stage == FilterCount)
            {
                await InvokeInner();
            }
            else
            {
                throw new InvalidOperationException($"{GetType()}.{nameof(Invoke)}() received an invalid call.");
            }
        }
        finally
        {
            _stage--;
        }
    }

    protected abstract Task InvokeFilter(int index);
    protected abstract string GetFilterName(int index);
    protected abstract Task InvokeInner();

    internal Response TakeResponse()
    {
        var result = _response!;
        _response = null;
        _ownedResponses?.Remove(result);
        return result;
    }

    public void Dispose()
    {
        var current = _response;
        _response = null;
        var owned = _ownedResponses;
        _ownedResponses = null;
        if (current is not null) owned?.Remove(current);
        try
        {
            current?.Dispose();
        }
        finally
        {
            if (owned is not null)
                foreach (var response in owned) response.Dispose();
        }
    }
}
