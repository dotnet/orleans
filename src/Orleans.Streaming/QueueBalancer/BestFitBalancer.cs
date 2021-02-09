using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Streams
{
    /// <summary>
    /// Best fit balancer keeps each active bucket responsible for its ideal set of resources, and redistributes 
    /// resources from inactive buckets evenly over active buckets.  If there are large numbers of inactive buckets,
    /// this can lead to quite a bit of shuffling of resources from inactive buckets as buckets come back online.
    /// Requirements:
    /// - Even distribution of resources across buckets
    /// - Must be consistent results for same inputs regardless of input order.
    /// - Minimize movement of resources when rebalancing from changes in active buckets.
    /// - Must be deterministic independent of previous distribution state.
    /// Algorithm:
    /// - On creation generate an ideal distribution of resources across all buckets, that is, each bucket has no more than 1 resource more
    ///    than any other bucket.
    /// - When requesting new resource distribution for a list of active buckets:
    ///     1) Initialize the new distribution of each active bucket with the ideal resources for that bucket.  This prevents
    ///        these resources from ever being assigned to another bucket unless a bucket becomes inactive.
    ///     2) Build a list of inactive buckets.
    ///     3) For each inactive bucket, add its ideal resource allocation to the list of resources to be reallocated.
    ///     4) Order the active buckets by the number of resources allocated to each and begin assigning them more resources 
    ///        from the list of resources to be reallocated.
    ///         i) Continue iterating over the active buckets assigning resources until there are no more resources that need
    ///            reallocated.
    /// </summary>
    /// <typeparam name="TBucket">Type of bucket upon which resources will be distributed among</typeparam>
    /// <typeparam name="TResource">Type of resources being distributed</typeparam>
    internal class BestFitBalancer<TBucket, TResource>
        where TBucket : IEquatable<TBucket>, IComparable<TBucket>
        where TResource : IEquatable<TResource>, IComparable<TResource>
    {
        private readonly Dictionary<TBucket, List<TResource>> idealDistribution;

        public Dictionary<TBucket, List<TResource>> IdealDistribution { get { return idealDistribution; } }

        /// <summary>
        /// Constructor.
        /// Initializes an ideal distribution to be used to aid in resource to bucket affinity.
        /// </summary>
        /// <param name="buckets">Buckets among which to distribute resources.</param>
        /// <param name="resources">Resources to be distributed.</param>
        public BestFitBalancer(IEnumerable<TBucket> buckets, IEnumerable<TResource> resources)
        {
            if (buckets == null)
            {
                throw new ArgumentNullException("buckets");
            }

            if (resources == null)
            {
                throw new ArgumentNullException("resources");
            }

            idealDistribution = BuildIdealDistribution(buckets, resources);
        }

        /// <summary>
        /// Gets a distribution for the active buckets. 
        /// Any active buckets keep their ideal distribution.  Resources from inactive buckets are redistributed evenly
        /// among the active buckets, starting with those with the fewest allocated resources.
        /// </summary>
        /// <param name="activeBuckets">currently active buckets</param>
        /// <returns></returns>
        public Dictionary<TBucket, List<TResource>> GetDistribution(IEnumerable<TBucket> activeBuckets)
        {
            if (activeBuckets == null)
            {
                throw new ArgumentNullException("activeBuckets");
            }

            // sanitize active buckets.  Remove duplicates, ensure all buckets are valid
            HashSet<TBucket> activeBucketsSet = new HashSet<TBucket>(activeBuckets);
            foreach (var bucket in activeBucketsSet)
            {
                if (!idealDistribution.ContainsKey(bucket))
                {
                    throw new ArgumentOutOfRangeException("activeBuckets", String.Format("Active buckets contain a bucket {0} not in the master list.", bucket));
                }
            }

            var newDistribution = new Dictionary<TBucket, List<TResource>>();
            // if no buckets, return empty resource distribution
            if (activeBucketsSet.Count == 0)
            {
                return newDistribution;
            }

            // setup ideal distribution for active buckets and build list of all resources that need redistributed from inactive buckets
            var resourcesToRedistribute = new List<TResource>();
            foreach (var kv in idealDistribution)
            {
                if (activeBucketsSet.Contains(kv.Key))
                    newDistribution.Add(kv.Key, kv.Value);
                else
                    resourcesToRedistribute.AddRange(kv.Value);
            }

            // redistribute remaining resources across the resource lists of the active buckets, resource lists with the fewest reasources first
            IOrderedEnumerable<List<TResource>> sortedResourceLists = newDistribution.Values.OrderBy(resources => resources.Count);
            IEnumerator<List<TResource>> resourceListenumerator = sortedResourceLists.GetEnumerator();
            foreach (TResource resource in resourcesToRedistribute)
            {
                // if we reach the end, start over
                if (!resourceListenumerator.MoveNext())
                {
                    resourceListenumerator = sortedResourceLists.GetEnumerator();
                    resourceListenumerator.MoveNext();
                }
                resourceListenumerator.Current.Add(resource);
            }

            return newDistribution;
        }

        /// <summary>
        /// Distribute resources evenly among buckets in a deterministic way.
        /// - Must distribute resources evenly regardless off order of inputs.
        /// </summary>
        /// <param name="buckets">Buckets among which to distribute resources.</param>
        /// <param name="resources">Resources to be distributed.</param>
        /// <returns>Dictionary of resources evenly distributed among the buckets</returns>
        private static Dictionary<TBucket, List<TResource>> BuildIdealDistribution(IEnumerable<TBucket> buckets, IEnumerable<TResource> resources)
        {
            var idealDistribution = new Dictionary<TBucket, List<TResource>>();

            // Sanitize buckets.  Remove duplicates and sort
            List<TBucket> bucketList = buckets.Distinct().ToList();
            if (bucketList.Count == 0)
            {
                return idealDistribution;
            }
            bucketList.Sort();

            // Sanitize resources.  Removed duplicates and sort
            List<TResource> resourceList = resources.Distinct().ToList();
            resourceList.Sort();

            // Distribute resources evenly among buckets
            var upperResourceCountPerBucket = (int)Math.Ceiling((double)resourceList.Count / bucketList.Count);
            var lowerResourceCountPerBucket = upperResourceCountPerBucket - 1;
            List<TResource>.Enumerator resourceEnumerator = resourceList.GetEnumerator();
            int bucketsToFillWithUpperResource = resourceList.Count % bucketList.Count;
            // a bucketsToFillWithUpperResource of 0 indicates resources are evenly devisible, so fill them all with upper resource count
            if (bucketsToFillWithUpperResource == 0)
            {
                bucketsToFillWithUpperResource = bucketList.Count;
            }
            int bucketsFilledCount = 0;
            foreach (TBucket bucket in bucketList)
            {
                // if we've filled the first bucketsToFillWithUpperResource buckets with upperResourceCountPerBucket
                //   resources, fill the rest with lowerResourceCountPerBucket
                int resourcesToAddToBucket = bucketsFilledCount < bucketsToFillWithUpperResource
                    ? upperResourceCountPerBucket
                    : lowerResourceCountPerBucket;
                var bucketResources = new List<TResource>();
                idealDistribution.Add(bucket, bucketResources);
                while (resourceEnumerator.MoveNext())
                {
                    bucketResources.Add(resourceEnumerator.Current);
                    if (bucketResources.Count >= resourcesToAddToBucket)
                    {
                        break;
                    }
                }
                bucketsFilledCount++;
            }
            return idealDistribution;
        }
    }
}
