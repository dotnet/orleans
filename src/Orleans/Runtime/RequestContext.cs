using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
#if !NETSTANDARD
using System.Runtime.Remoting.Messaging;
#endif
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
        internal const string ORLEANS_REQUEST_CONTEXT_KEY = "#ORL_RC";
        internal const string PING_APPLICATION_HEADER = "Ping";

#if NETSTANDARD
        public static readonly AsyncLocal<Guid> ActivityId = new AsyncLocal<Guid>();
        private static readonly AsyncLocal<Dictionary<string, object>> CallContextData = new AsyncLocal<Dictionary<string, object>>();
#endif

        /// <summary>
        /// Retrieve a value from the RequestContext key-value bag.
        /// </summary>
        /// <param name="key">The key for the value to be retrieved.</param>
        /// <returns>The value currently in the RequestContext for the specified key, 
        /// otherwise returns <c>null</c> if no data is present for that key.</returns>
        public static object Get(string key)
        {
            Dictionary<string, object> values = GetContextData();
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
            Dictionary<string, object> values = GetContextData();

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
            SetContextData(values);
        }

        /// <summary>
        /// Remove a value from the RequestContext key-value bag.
        /// </summary>
        /// <param name="key">The key for the value to be removed.</param>
        /// <returns>Boolean <c>True</c> if the value was previously in the RequestContext key-value bag and has now been removed, otherwise returns <c>False</c>.</returns>
        public static bool Remove(string key)
        {
            Dictionary<string, object> values = GetContextData();

            if (values == null || values.Count == 0 || !values.ContainsKey(key))
            {
                return false;
            }
            var newValues = new Dictionary<string, object>(values);
            bool retValue = newValues.Remove(key);
            SetContextData(newValues);
            return retValue;
        }

        public static void Import(Dictionary<string, object> contextData)
        {
            if (PropagateActivityId)
            {
                object activityIdObj;
                if (contextData == null || !contextData.TryGetValue(E2_E_TRACING_ACTIVITY_ID_HEADER, out activityIdObj))
                {
                    activityIdObj = Guid.Empty;
                }

#if NETSTANDARD
                ActivityId.Value = (Guid) activityIdObj;
#else
                Trace.CorrelationManager.ActivityId = (Guid)activityIdObj;
#endif
            }
            if (contextData != null && contextData.Count > 0)
            {
                var values = contextData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                // We have some data, so store RC data into LogicalCallContext
                SetContextData(values);
            }
            else
            {
                // Clear any previous RC data from LogicalCallContext.
                // MUST CLEAR the LLC, so that previous request LLC does not leak into this one.
                Clear();
            }
        }

        public static Dictionary<string, object> Export()
        {
            Dictionary<string, object> values = GetContextData();

            if (PropagateActivityId)
            {
#if !NETSTANDARD
                var activityId = Trace.CorrelationManager.ActivityId;
#else
                var activityId = ActivityId.Value;
#endif
                if (activityId != Guid.Empty)
                {
                    values = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values); // Create new copy before mutating data
                    values[E2_E_TRACING_ACTIVITY_ID_HEADER] = activityId;
                    // We have some changed data, so write RC data back into LogicalCallContext
                    SetContextData(values);
                }
            }
            if (values != null && values.Count != 0)
                return values.ToDictionary(kvp => kvp.Key, kvp => SerializationManager.DeepCopy(kvp.Value));
            return null;
        }

        public static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
#if !NETSTANDARD
            CallContext.FreeNamedDataSlot(ORLEANS_REQUEST_CONTEXT_KEY);
#else
            CallContextData.Value = null;
#endif
        }

        private static void SetContextData(Dictionary<string, object> values)
        {
#if !NETSTANDARD
            CallContext.LogicalSetData(ORLEANS_REQUEST_CONTEXT_KEY, values);
#else
            CallContextData.Value = values;
#endif
        }

        private static Dictionary<string, object> GetContextData()
        {
#if !NETSTANDARD
            return (Dictionary<string, object>) CallContext.LogicalGetData(ORLEANS_REQUEST_CONTEXT_KEY);
#else
            return CallContextData.Value;
#endif
        }
    }
}
