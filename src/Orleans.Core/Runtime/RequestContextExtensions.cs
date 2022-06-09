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
        public static void Import(Dictionary<string, object> contextData) => Import(contextData, null); 

        /// <summary>
        /// Imports the specified context data into the current <see cref="RequestContext"/>, clearing all existing values.
        /// </summary>
        /// <param name="contextData">The context data.</param>
        /// <param name="requestMessage">The request message, or <see langword="null"/> if no request is present.</param>
        internal static void Import(Dictionary<string, object> contextData, object requestMessage)
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

            var values = contextData switch
            {
                { Count: > 0 } => contextData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                _ => null,
            };

            RequestContext.CallContextData.Value = new RequestContext.ContextProperties
            {
                RequestObject = requestMessage,
                Values = values,
            };
        }

        /// <summary>
        /// Exports a copy of the current <see cref="RequestContext"/>.
        /// </summary>
        /// <param name="copier">The copier.</param>
        /// <returns>A copy of the current request context.</returns>
        public static Dictionary<string, object> Export(DeepCopier copier)
        {
            var (values, _) = ExportInternal(copier);
            return values;
        }

        /// <summary>
        /// Exports a copy of the current <see cref="RequestContext"/>.
        /// </summary>
        /// <param name="copier">The copier.</param>
        /// <returns>A copy of the current request context.</returns>
        internal static (Dictionary<string, object> Values, object RequestObject) ExportInternal(DeepCopier copier)
        {
            var properties = RequestContext.CallContextData.Value;
            var values = properties.Values;

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

            var resultValues = (values != null && values.Count > 0)
                ? copier.Copy(values)
                : null;
            return (resultValues, properties.RequestObject);
        }

        /// <summary>
        /// Suppresses the flow of the currently executing call chain.
        /// </summary>
        internal static object SuppressCurrentCallChainFlow()
        {
            var properties = RequestContext.CallContextData.Value;
            var result = properties.RequestObject;
            if (result is not null)
            {
                RequestContext.CallContextData.Value = new RequestContext.ContextProperties
                {
                    Values = properties.Values,
                };
            }

            return result;
        }

        /// <summary>
        /// Restores the flow of a previously suppressed call chain.
        /// </summary>
        internal static void RestoreCurrentCallChainFlow(object requestMessage)
        {
            if (requestMessage is not null)
            {
                var properties = RequestContext.CallContextData.Value;
                RequestContext.CallContextData.Value = new RequestContext.ContextProperties
                {
                    Values = properties.Values,
                    RequestObject = requestMessage,
                };
            }
        }
    }
}
