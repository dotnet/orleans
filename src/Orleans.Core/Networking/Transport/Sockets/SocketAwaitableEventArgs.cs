// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace Orleans.Connections.Transport.Sockets;

// A slimmed down version of https://github.com/dotnet/runtime/blob/82ca681cbac89d813a3ce397e0c665e6c051ed67/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.Tasks.cs#L798 that
// 1. Doesn't support any custom scheduling other than the PipeScheduler (no sync context, no task scheduler)
// 2. Doesn't do ValueTask validation using the token
// 3. Doesn't support usage outside of async/await (doesn't try to capture and restore the execution context)
// 4. Doesn't use cancellation tokens
internal class SocketAwaitableEventArgs : SocketAsyncEventArgs, IValueTaskSource
{
    private static readonly Action<object?> ContinuationCompleted = _ => { };
    private Action<object?>? _continuation;

    public SocketAwaitableEventArgs()
        : base(unsafeSuppressExecutionContextFlow: true)
    {
    }

    public bool IsCompleted { get; private set; }

    public Exception? Error => CreateException(SocketError);

    [MemberNotNullWhen(true, nameof(Error))]
    public bool HasError => SocketError != SocketError.Success;

    protected override void OnCompleted(SocketAsyncEventArgs _)
    {
        IsCompleted = true;
        var continuation = _continuation;

        if (continuation != null || (continuation = Interlocked.CompareExchange(ref _continuation, ContinuationCompleted, null)) != null)
        {
            var state = UserToken;
            UserToken = null;
            _continuation = ContinuationCompleted; // in case someone's polling IsCompleted

            // Execute the continuation inline.
            continuation(state);
        }
    }

    public void GetResult(short token)
    {
        _continuation = null;
        IsCompleted = false;
        if (HasError) ThrowError();

        void ThrowError() => throw Error;
    }

    protected static SocketException? CreateException(SocketError e)
    {
        if (e is SocketError.Success) return null;
        return new SocketException((int)e);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        return !ReferenceEquals(_continuation, ContinuationCompleted) ? ValueTaskSourceStatus.Pending :
                SocketError == SocketError.Success ? ValueTaskSourceStatus.Succeeded :
                ValueTaskSourceStatus.Faulted;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        UserToken = state;
        var prevContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);
        if (ReferenceEquals(prevContinuation, ContinuationCompleted))
        {
            UserToken = null;

            // Execute the continuation inline.
            continuation(state);
        }
    }
}
