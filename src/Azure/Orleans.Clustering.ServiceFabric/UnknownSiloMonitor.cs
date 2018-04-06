using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Clustering.ServiceFabric
{
    /// <summary>
    /// Monitors cluster changes for information about silos which are defunct and have not been reported as functional by Service Fabric.
    /// </summary>
    internal class UnknownSiloMonitor
    {
        /// <summary>
        /// Collection of unknown silos.
        /// </summary>
        private readonly ConcurrentDictionary<SiloAddress, DateTime> unknownSilos = new ConcurrentDictionary<SiloAddress, DateTime>();
        private readonly ServiceFabricClusteringOptions options;
        private readonly ILogger<UnknownSiloMonitor> log;

        public UnknownSiloMonitor(IOptions<ServiceFabricClusteringOptions> options, ILogger<UnknownSiloMonitor> log)
        {
            this.options = options.Value;
            this.log = log;
        }

        /// <summary>
        /// Gets or sets the delegate used to retrieve the current time.
        /// </summary>
        /// <remarks>
        /// This is exposed for testing purposes.
        /// The accuracy of this method
        /// </remarks>
        internal Func<DateTime> GetDateTime { get; set; } = () => DateTime.UtcNow;
        
        /// <summary>
        /// Adds an unknown silo to monitor.
        /// </summary>
        /// <param name="siloAddress">The silo address.</param>
        /// <returns><see langword="true"/> if the silo was added as an unknown silo, <see langword="false"/> otherwise.</returns>
        public void ReportUnknownSilo(SiloAddress siloAddress)
        {
            if (this.unknownSilos.TryAdd(siloAddress, this.GetDateTime()))
            {
                this.log.Info($"Recording unknown silo {siloAddress}.");
            }
        }

        /// <summary>
        /// Finds dead silos which were previously in an unknown state.
        /// </summary>
        /// <param name="allKnownSilos">The collection of all known silos, including dead silos.</param>
        /// <returns>A collection of dead silos.</returns>
        public IEnumerable<SiloAddress> DetermineDeadSilos(Dictionary<SiloAddress, SiloStatus> allKnownSilos)
        {
            if (this.unknownSilos.Count == 0) return Array.Empty<SiloAddress>();
            
            // The latest generation for each silo endpoint.
            var latestGenerations = new Dictionary<IPEndPoint, int>();

            // All known silos can be removed from the unknown list as long as their status is valid.
            foreach (var known in allKnownSilos)
            {
                if (known.Value == SiloStatus.None) continue;
                var address = known.Key;
                var endpoint = address.Endpoint;
                if (!latestGenerations.TryGetValue(endpoint, out var knownGeneration) || knownGeneration < address.Generation)
                {
                    latestGenerations[endpoint] = address.Generation;
                }
                
                // Unknown silos are not removed from the collection until they have been confirmed in the collection of known silos.
                if (this.unknownSilos.TryRemove(address, out var _))
                {
                    this.log.Info($"Previously unknown silo {address} has transitioned to state {known.Value}.");
                }
            }
            
            var updates = new List<SiloAddress>();
            foreach (var pair in this.unknownSilos)
            {
                var unknownSilo = pair.Key;

                // If a known silo exists on the endpoint with a higher generation, the old silo must be dead.
                if (latestGenerations.TryGetValue(unknownSilo.Endpoint, out var knownGeneration) && knownGeneration > unknownSilo.Generation)
                {
                    this.log.Info($"Previously unknown silo {unknownSilo} was superseded by later generation on same endpoint {SiloAddress.New(unknownSilo.Endpoint, knownGeneration)}.");
                    updates.Add(unknownSilo);
                }
                
                // Silos which have been in an unknown state for more than configured maximum allowed time are automatically considered dead.
                if (this.GetDateTime() - pair.Value > this.options.UnknownSiloRemovalPeriod)
                {
                    this.log.Info($"Previously unknown silo {unknownSilo} declared dead after {this.options.UnknownSiloRemovalPeriod.TotalSeconds} seconds.");
                    updates.Add(unknownSilo);
                }
            }

            if (this.unknownSilos.Count == 0 && updates.Count == 0)
            {
                this.log.Info("All unknown silos have been identified.");
            }

            return updates;
        }
    }
}