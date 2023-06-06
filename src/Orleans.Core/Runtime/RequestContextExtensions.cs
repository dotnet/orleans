#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Extensions for working with <see cref="RequestContext"/>.
    /// </summary>
    public static class RequestContextExtensions
    {
        /// <summary>
        /// Imports the specified context data into the current <see cref="RequestContext"/>, clearing all existing values.
        /// </summary>
        /// <param name="contextData">The context data.</param>
        public static void Import(Dictionary<string, object>? contextData)
        {
            var values = contextData switch
            {
                { Count: > 0 } => contextData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                _ => null,
            };

            RequestContext.CallContextData.Value = new RequestContext.ContextProperties
            {
                Values = values,
            };
        }

        /// <summary>
        /// Exports a copy of the current <see cref="RequestContext"/>.
        /// </summary>
        /// <param name="copier">The copier.</param>
        /// <returns>A copy of the current request context.</returns>
        public static Dictionary<string, object>? Export(DeepCopier copier)
        {
            var properties = RequestContext.CallContextData.Value;
            var values = properties.Values;

            var resultValues = (values != null && values.Count > 0)
                ? copier.Copy(values)
                : null;
            return resultValues;
        }

        internal static Guid GetReentrancyId(this Message message) => GetReentrancyId(message?.RequestContextData);

        internal static Guid GetReentrancyId(Dictionary<string, object>? contextData)
        {
            if (contextData is not { Count: > 0 }) return Guid.Empty;
            _ = contextData.TryGetValue(RequestContext.CALL_CHAIN_REENTRANCY_HEADER, out var reentrancyId);
            return reentrancyId is Guid guid ? guid : Guid.Empty;
        }
    }
}
