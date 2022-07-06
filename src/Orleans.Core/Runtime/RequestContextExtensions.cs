using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
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
                PropagateActivityIdToCorrelationManager(contextData);
            }

            RequestContext.CallContextData.Value = contextData;

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void PropagateActivityIdToCorrelationManager(Dictionary<string, object> contextData)
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
                ExportActivityId(ref values);
            }

            return values switch
            {
                { Count: > 0 } => copier.Copy(values),
                _ => null
            };

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void ExportActivityId(ref Dictionary<string, object> values)
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
        }
    }
}
