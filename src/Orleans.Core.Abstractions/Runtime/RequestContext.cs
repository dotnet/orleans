using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;

namespace Orleans.Runtime
{
    /// <summary>
    /// This class holds information regarding the request currently being processed.
    /// It is explicitly intended to be available to application code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request context is represented as a property bag.
    /// Some values are provided by default; others are derived from messages headers in the
    /// request that led to the current processing.
    /// </para>
    /// <para>
    /// Information stored in <see cref="RequestContext"/> is propagated from Orleans clients to Orleans grains automatically by the Orleans runtime.
    /// </para>
    /// </remarks>
    public static class RequestContext
    {
        /// <summary>
        /// Gets or sets a value indicating whether <c>Trace.CorrelationManager.ActivityId</c> settings should be propagated into grain calls.
        /// </summary>
        public static bool PropagateActivityId { get; set; }

        internal const string CALL_CHAIN_REQUEST_CONTEXT_HEADER = "#RC_CCH";
        internal const string E2_E_TRACING_ACTIVITY_ID_HEADER = "#RC_AI";
        internal const string PING_APPLICATION_HEADER = "Ping";

        internal static readonly AsyncLocal<Dictionary<string, object>> CallContextData = new();

        /// <summary>Gets or sets an activity ID that can be used for correlation.</summary>
        public static Guid ActivityId
        {
            get { return (Guid)(Get(E2_E_TRACING_ACTIVITY_ID_HEADER) ?? Guid.Empty); }
            set
            {
                if (value == Guid.Empty)
                {
                    Remove(E2_E_TRACING_ACTIVITY_ID_HEADER);
                }
                else
                {
                    Set(E2_E_TRACING_ACTIVITY_ID_HEADER, value);
                }
            }
        }

        /// <summary>
        /// Retrieves a value from the request context.
        /// </summary>
        /// <param name="key">The key for the value to be retrieved.</param>
        /// <returns>
        /// The value currently associated with the provided key, otherwise <see langword="null"/> if no data is present for that key.
        /// </returns>
        public static object Get(string key)
        {
            var values = CallContextData.Value;

            if (values != null && values.TryGetValue(key, out var result))
            {
                return result;
            }

            return null;
        }

        /// <summary>
        /// Sets a value in the request context.
        /// </summary>
        /// <param name="key">The key for the value to be updated or added.</param>
        /// <param name="value">The value to be stored into the request context.</param>
        public static void Set(string key, object value)
        {
            var values = CallContextData.Value;

            if (values == null)
            {
                values = new Dictionary<string, object>(1);
            }
            else
            {
                // Have to copy the actual Dictionary value, mutate it and set it back.
                // This is since AsyncLocal copies link to dictionary, not create a new one.
                // So we need to make sure that modifying the value, we doesn't affect other threads.
                var hadPreviousValue = values.ContainsKey(key);
                var newValues = new Dictionary<string, object>(values.Count + (hadPreviousValue ? 0 : 1));
                foreach (var pair in values)
                {
                    newValues.Add(pair.Key, pair.Value);
                }

                values = newValues;
            }

            values[key] = value;
            CallContextData.Value = values;
        }

        /// <summary>
        /// Remove a value from the request context.
        /// </summary>
        /// <param name="key">The key for the value to be removed.</param>
        /// <returns><see langword="true"/> if the value was previously in the request context and has now been removed, otherwise <see langword="false"/>.</returns>
        public static bool Remove(string key)
        {
            var values = CallContextData.Value;

            if (values == null || values.Count == 0 || !values.ContainsKey(key))
            {
                return false;
            }

            if (values.Count == 1)
            {
                CallContextData.Value = null;
            }
            else
            {
                var newValues = new Dictionary<string, object>(values);
                newValues.Remove(key);
                CallContextData.Value = newValues;
            }

            return true;
        }

        /// <summary>
        /// Clears the current request context.
        /// </summary>
        public static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
            CallContextData.Value = default;
        }

        internal static object CurrentRequest
        {
            get => OrleansSynchronizationContext.Current switch
            {
                null or { IsRequestFlowSuppressed: true } => default,
                { } context => context.CurrentRequest
            };

            set
            {
                var context = OrleansSynchronizationContext.Current;
                if (context is not null)
                {
                    context.CurrentRequest = value;
                }
            }
        }

        internal static void SetCurrentRequest(object requestMessage) => OrleansSynchronizationContext.Current.CurrentRequest = requestMessage;

        internal static SuppressedCallChainFlow SuppressCallChainFlow()
        {
            return OrleansSynchronizationContext.Current switch
            {
                { IsRequestFlowSuppressed: true } ctx => new SuppressedCallChainFlow(ctx),
                _ => default
            };
        }

        internal static void RestoreCallChainFlow()
        {
            var context = OrleansSynchronizationContext.Current;
            if (context is not null) context.IsRequestFlowSuppressed = false;
        }
    }

    internal readonly struct SuppressedCallChainFlow : IDisposable
    {
        private readonly OrleansSynchronizationContext _context;
        public SuppressedCallChainFlow(OrleansSynchronizationContext context) => _context = context;
        public bool IsCallChainFlowSuppressed => _context is not null;
        public void Dispose()
        {
            if (_context is { } ctx) ctx.IsRequestFlowSuppressed = false;
        }
    }

    internal abstract class OrleansSynchronizationContext : SynchronizationContext
    {
        public static new OrleansSynchronizationContext Current => SynchronizationContext.Current as OrleansSynchronizationContext;

        public static OrleansSynchronizationContext Fork(OrleansSynchronizationContext original)
        {
            var innerContext = original switch
            {
                RequestSynchronizationContext wrapped => wrapped.InnerContext,
                _ => original
            };

            return new RequestSynchronizationContext(innerContext)
            {
                CurrentRequest = original.CurrentRequest,
            };
        }

        public abstract object CurrentRequest { get; set; }
        public abstract IGrainContext GrainContext { get; }
        public bool IsRequestFlowSuppressed { get; set; }

        public override SynchronizationContext CreateCopy() => Fork(this);
    }

    internal sealed class RequestSynchronizationContext : OrleansSynchronizationContext
    {
        public RequestSynchronizationContext(OrleansSynchronizationContext inner)
        {
            if (inner is RequestSynchronizationContext)
            {
                ThrowInvalidArgumentException();
            }

            InnerContext = inner;
        }

        public OrleansSynchronizationContext InnerContext { get; init; }

        public override object CurrentRequest { get; set; }

        public override IGrainContext GrainContext => InnerContext.GrainContext;

        public override void Send(SendOrPostCallback callback, object state) => InnerContext.Send(callback, state);

        public override void Post(SendOrPostCallback callback, object state) => InnerContext.Post(callback, state);

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidArgumentException() => throw new ArgumentException();

        /// <inheritdoc/>
        public override SynchronizationContext CreateCopy()
        {
            return new RequestSynchronizationContext(InnerContext)
            {
                CurrentRequest = CurrentRequest,
            };
        }
    }

    internal sealed class ThreadPoolSynchronizationContext : OrleansSynchronizationContext
    {
        public ThreadPoolSynchronizationContext(IGrainContext grainContext)
        {
            GrainContext = grainContext;
        }

        public override IGrainContext GrainContext { get; }
        public override object CurrentRequest { get => default; set => throw new NotSupportedException(); }
        public override void Send(SendOrPostCallback callback, object state) => callback(state);

        public override void Post(SendOrPostCallback callback, object state) => ThreadPool.UnsafeQueueUserWorkItem(s => callback(s), state, preferLocal: true);
    }
}
