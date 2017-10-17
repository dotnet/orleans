using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Orleans.Serialization;

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
    /// Information stored in RequestContext is propagated from 
    /// Orleans clients to Orleans grains automatically 
    /// by the Orleans runtime.
    /// </para>
    /// </remarks>
    public static class RequestContext
    {
        /// <summary>
        /// Whether Trace.CorrelationManager.ActivityId settings should be propagated into grain calls.
        /// </summary>
        public static bool PropagateActivityId { get; set; }

        internal const string CALL_CHAIN_REQUEST_CONTEXT_HEADER = "#RC_CCH";
        internal const string E2_E_TRACING_ACTIVITY_ID_HEADER = "#RC_AI";
        internal const string PING_APPLICATION_HEADER = "Ping";

        private static readonly AsyncLocal<Dictionary<string, object>> CallContextData = new AsyncLocal<Dictionary<string, object>>();

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
        /// Retrieve a value from the RequestContext key-value bag.
        /// </summary>
        /// <param name="key">The key for the value to be retrieved.</param>
        /// <returns>The value currently in the RequestContext for the specified key, 
        /// otherwise returns <c>null</c> if no data is present for that key.</returns>
        public static object Get(string key)
        {
            var values = CallContextData.Value;
            object result;
            if ((values != null) && values.TryGetValue(key, out result))
            {
                return result;
            }
            return null;
        }

        /// <summary>
        /// Sets a value into the RequestContext key-value bag.
        /// </summary>
        /// <param name="key">The key for the value to be updated / added.</param>
        /// <param name="value">The value to be stored into RequestContext.</param>
        public static void Set(string key, object value)
        {
            var values = CallContextData.Value;

            if (values == null)
            {
                values = new Dictionary<string, object>();
            }
            else
            {
                // Have to copy the actual Dictionary value, mutate it and set it back.
                // http://blog.stephencleary.com/2013/04/implicit-async-context-asynclocal.html
                // This is since LLC is only copy-on-write copied only upon LogicalSetData.
                values = new Dictionary<string, object>(values);
            }
            values[key] = value;
            CallContextData.Value = values;
        }

        /// <summary>
        /// Remove a value from the RequestContext key-value bag.
        /// </summary>
        /// <param name="key">The key for the value to be removed.</param>
        /// <returns>Boolean <c>True</c> if the value was previously in the RequestContext key-value bag and has now been removed, otherwise returns <c>False</c>.</returns>
        public static bool Remove(string key)
        {
            var values = CallContextData.Value;

            if (values == null || values.Count == 0 || !values.ContainsKey(key))
            {
                return false;
            }
            var newValues = new Dictionary<string, object>(values);
            bool retValue = newValues.Remove(key);
            CallContextData.Value = newValues;
            return retValue;
        }

        public static void Import(Dictionary<string, object> contextData)
        {
            if (PropagateActivityId)
            {
                object activityIdObj = Guid.Empty;
                if (contextData?.TryGetValue(E2_E_TRACING_ACTIVITY_ID_HEADER, out activityIdObj) == true)
                {
                    Trace.CorrelationManager.ActivityId = (Guid)activityIdObj;
                }
                else
                {
                    Trace.CorrelationManager.ActivityId = Guid.Empty;
                }
            }

            if (contextData != null && contextData.Count > 0)
            {
                var values = contextData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                // We have some data, so store RC data into the async local field.
                CallContextData.Value = values;
            }
            else
            {
                // Clear any previous RC data from the async local field.
                // MUST CLEAR the LLC, so that previous request LLC does not leak into this one.
                Clear();
            }
        }

        public static Dictionary<string, object> Export(SerializationManager serializationManager)
        {
            var values = CallContextData.Value;

            if (PropagateActivityId)
            {
                var activityIdOverride = Trace.CorrelationManager.ActivityId;
                if (activityIdOverride != Guid.Empty)
                {
                    object existingActivityId;
                    if (values == null 
                        || !values.TryGetValue(E2_E_TRACING_ACTIVITY_ID_HEADER, out existingActivityId)
                        || activityIdOverride != (Guid)existingActivityId)
                    {
                        // Create new copy before mutating data
                        values = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
                        values[E2_E_TRACING_ACTIVITY_ID_HEADER] = activityIdOverride;
                    }
                }
            }

            return (values != null && values.Count > 0)
                ? (Dictionary<string, object>)serializationManager.DeepCopy(values)
                : null;
        }

        public static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
            if (CallContextData.Value != null)
            {
                CallContextData.Value = null;
            }
        }
    }
}
