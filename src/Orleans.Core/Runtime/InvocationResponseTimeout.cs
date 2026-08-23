using System;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

internal static class InvocationResponseTimeout
{
    private static readonly ConditionalWeakTable<IInvokable, TimeoutOverride> Overrides = new();

    public static TimeSpan? Get(IInvokable request)
        => Overrides.TryGetValue(request, out var value) ? value.Timeout : request.GetDefaultResponseTimeout();

    public static void Set(IInvokable request, TimeSpan timeout)
        => Overrides.GetValue(request, static _ => new()).Timeout = timeout;

    private sealed class TimeoutOverride
    {
        public TimeSpan Timeout { get; set; }
    }
}
