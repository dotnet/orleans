using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Extensions for working wiht <see cref="RequestContext"/>.
    /// </summary>
    public static class RequestContextExtensions
    {
        /// <summary>
        /// Imports the specified context data into the current <see cref="RequestContext"/>, clearing all existing values.
        /// </summary>
        /// <param name="contextData">The context data.</param>
        public static void Import(Dictionary<string, object> contextData)
        {
            if (RequestContext.PropagateActivityId)
            {
                object activityIdObj = Guid.Empty;
                if (contextData?.TryGetValue(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, out activityIdObj) == true)
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
                RequestContext.CallContextData.Value = values;
            }
            else
            {
                // Clear any previous RC data from the async local field.
                // MUST CLEAR the LLC, so that previous request LLC does not leak into this one.
                RequestContext.Clear();
            }
        }

        /// <summary>
        /// Exports a copy of the current <see cref="RequestContext"/>.
        /// </summary>
        /// <param name="copier">The copier.</param>
        /// <returns>A copy of the current request context.</returns>
        public static Dictionary<string, object> Export(DeepCopier copier)
        {
            var values = RequestContext.CallContextData.Value;

            if (RequestContext.PropagateActivityId)
            {
                var activityIdOverride = Trace.CorrelationManager.ActivityId;
                if (activityIdOverride != Guid.Empty)
                {
                    object existingActivityId;
                    if (values == null
                        || !values.TryGetValue(RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER, out existingActivityId)
                        || activityIdOverride != (Guid)existingActivityId)
                    {
                        // Create new copy before mutating data
                        values = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
                        values[RequestContext.E2_E_TRACING_ACTIVITY_ID_HEADER] = activityIdOverride;
                    }
                }
            }

            return (values != null && values.Count > 0)
                ? copier.Copy(values)
                : null;
        }
    }
}
