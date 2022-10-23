using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// A placement strategy which attempts to achieve approximately even load based upon the number of recently-active grains on each server.
    /// </summary>
    /// <remarks>
    /// The intention of this placement strategy is to place new grain activations on the least heavily loaded server based on the number of recently busy grains.
    /// It includes a mechanism in which all servers periodically publish their total activation count to all other servers.
    /// The placement director then selects a server which is predicted to have the fewest activations by examining the most recently
    /// reported activation count and a making prediction of the current activation count based upon the recent activation count made by
    /// the placement director on the current server. The director selects a number of servers at random when making this prediction,
    /// in an attempt to avoid multiple separate servers overloading the same server. By default, two servers are selected at random,
    /// but this value is configurable via <c>Orleans.Runtime.ActivationCountBasedPlacementOptions</c>.
    /// <br/>
    /// This algorithm is based on the thesis The Power of Two Choices in Randomized Load Balancing by Michael David Mitzenmacher <see href="https://www.eecs.harvard.edu/~michaelm/postscripts/mythesis.pdf"/>,
    /// and is also used in NGINX for distributed load balancing, as described in the article NGINX and the "Power of Two Choices" Load-Balancing Algorithm <see href="https://www.nginx.com/blog/nginx-power-of-two-choices-load-balancing-algorithm/"/>.
    /// <br/>
    /// This placement strategy is configured by adding the <see cref="Orleans.Placement.ActivationCountBasedPlacementAttribute"/> attribute to a grain.
    /// </remarks>
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class ActivationCountBasedPlacement : PlacementStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        internal static ActivationCountBasedPlacement Singleton { get; } = new ActivationCountBasedPlacement();
    }
}
