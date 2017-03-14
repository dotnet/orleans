using System.Collections.Generic;
using System.Fabric;
using Microsoft.Orleans.ServiceFabric.Models;
using Newtonsoft.Json;

namespace Microsoft.Orleans.ServiceFabric.Utilities
{
    internal static class ResolvedServicePartitionExtensions
    {
        /// <summary>
        /// Retrieves the active endpoints published by the specified partition.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <returns>The active endpoints published by the specified partition.</returns>
        public static List<FabricSiloInfo> GetPartitionEndpoints(this ResolvedServicePartition partition)
        {
            var results = new List<FabricSiloInfo>(partition.Endpoints.Count);
            foreach (var silo in partition.Endpoints)
            {
                // Find the primary endpoint. If this is a stateless service, find any endpoint.
                if (silo.Role != ServiceEndpointRole.Stateless && silo.Role != ServiceEndpointRole.StatefulPrimary) continue;

                // Read the endpoint details.
                var endpoints = JsonConvert.DeserializeObject<ServicePartitionEndpoints>(silo.Address);
                var orleansEndpoint = endpoints.Endpoints[OrleansServiceListener.OrleansServiceFabricEndpointName];
                results.Add(JsonConvert.DeserializeObject<FabricSiloInfo>(orleansEndpoint));
            }

            return results;
        }

        /// <summary>
        /// Respresents endpoints returned from <see cref="ResolvedServiceEndpoint.Address"/> in JSON form.
        /// </summary>
        internal class ServicePartitionEndpoints
        {
            /// <summary>
            /// Gets or sets the endpoints dictionary.
            /// </summary>
            public Dictionary<string, string> Endpoints { get; set; }
        }
    }
}