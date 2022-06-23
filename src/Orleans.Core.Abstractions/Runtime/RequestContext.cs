using System;
using System.Collections.Generic;
using System.Threading;

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

        internal static readonly AsyncLocal<ContextProperties> CallContextData = new AsyncLocal<ContextProperties>();

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
            var properties = CallContextData.Value;
            var values = properties.Values;

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
            var properties = CallContextData.Value;
            var values = properties.Values;

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
            CallContextData.Value = new ContextProperties
            {
                RequestObject = properties.RequestObject,
                Values = values
            };
        }

        /// <summary>
        /// Remove a value from the request context.
        /// </summary>
        /// <param name="key">The key for the value to be removed.</param>
        /// <returns><see langword="true"/> if the value was previously in the request context and has now been removed, otherwise <see langword="false"/>.</returns>
        public static bool Remove(string key)
        {
            var properties = CallContextData.Value;
            var values = properties.Values;

            if (values == null || values.Count == 0 || !values.ContainsKey(key))
            {
                return false;
            }

            if (values.Count == 1)
            {
                CallContextData.Value = new ContextProperties
                {
                    RequestObject = properties.RequestObject,
                    Values = null
                };
                return true;
            }
            else
            {
                var newValues = new Dictionary<string, object>(values);
                newValues.Remove(key);
                CallContextData.Value = new ContextProperties
                {
                    RequestObject = properties.RequestObject,
                    Values = newValues
                };
                return true;
            }
        }

        /// <summary>
        /// Clears the current request context.
        /// </summary>
        public static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
            if (!CallContextData.Value.IsDefault)
            {
                CallContextData.Value = default;
            }
        }

        /// <summary>
        /// Gets the currently executing request.
        /// </summary>
        internal static object RequestObject => CallContextData.Value.RequestObject;

        internal readonly struct ContextProperties
        {
            public object RequestObject { get; init; }

            public Dictionary<string, object> Values { get; init; }

            public bool IsDefault => RequestObject is null && Values is null;
        }
    }
}
