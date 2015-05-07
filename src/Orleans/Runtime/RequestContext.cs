/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
﻿using System.Linq;
﻿using System.Runtime.Remoting.Messaging;
﻿using Orleans.Serialization;


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
    /// RequestContext data is not automatically propagated across 
    /// TPL thread-switch boundaries -- <see cref="CallContext"/> 
    /// for that type of functionality.
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

        internal static void ImportFromMessage(Message msg)
        {
            var contextData = msg.RequestContextData;
            var values = contextData != null
                ? contextData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                : new Dictionary<string, object>();

            if (PropagateActivityId)
            {
                object activityIdObj;
                if (!values.TryGetValue(E2_E_TRACING_ACTIVITY_ID_HEADER, out activityIdObj))
                {
                    activityIdObj = Guid.Empty;
                }
                Trace.CorrelationManager.ActivityId = (Guid) activityIdObj;
            }
            if (values.Count > 0)
            {
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

        internal static void ExportToMessage(Message msg)
        {
            Dictionary<string, object> values = GetContextData();

            if (PropagateActivityId)
            {
                Guid activityId = Trace.CorrelationManager.ActivityId;
                if (activityId != Guid.Empty)
                {
                    values = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values); // Create new copy before mutating data
                    values[E2_E_TRACING_ACTIVITY_ID_HEADER] = activityId;
                    // We have some changed data, so write RC data back into LogicalCallContext
                    SetContextData(values);
                }
            }
            if (values != null && values.Count != 0)
                msg.RequestContextData = values.ToDictionary(kvp => kvp.Key, kvp => SerializationManager.DeepCopy(kvp.Value));
        }

        internal static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
            CallContext.FreeNamedDataSlot(ORLEANS_REQUEST_CONTEXT_KEY);
        }

        private static void SetContextData(Dictionary<string, object> values)
        {
            CallContext.LogicalSetData(ORLEANS_REQUEST_CONTEXT_KEY, values);
        }

        private static Dictionary<string, object> GetContextData()
        {
            return (Dictionary<string, object>) CallContext.LogicalGetData(ORLEANS_REQUEST_CONTEXT_KEY);
        }
    }
}
