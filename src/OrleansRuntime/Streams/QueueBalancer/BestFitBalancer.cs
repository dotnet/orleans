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

        /// <summary>
        /// Constructor.
        /// Initializes an ideal distribution to be used to aid in resource to bucket affinity.
        /// </summary>
        /// <param name="buckets">Buckets among which to destribute resources.</param>
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
            List<TBucket> activeBucketsList = activeBuckets.Distinct().ToList();
            if (activeBucketsList.Except(idealDistribution.Keys).Any())
            {
                throw new ArgumentOutOfRangeException("activeBuckets", "Active buckets contains buckets no in master list");
            }

            var newDistribution = new Dictionary<TBucket, List<TResource>>();
            // if no buckets, return empty resource distribution
            if (activeBucketsList.Count == 0)
            {
                return newDistribution;
            }

            // sanitize inputs.  Stort active buckets
            activeBucketsList.Sort();
            
            // setup ideal distribution for active buckets
            foreach (TBucket bucket in idealDistribution.Keys.Where(activeBucketsList.Contains))
            {
                newDistribution.Add(bucket, idealDistribution[bucket].ToList());
            }

            // get list of inactive buckets
            List<TBucket> inactiveBuckets = idealDistribution.Keys.Where(bucket => !activeBucketsList.Contains(bucket)).ToList();

            // build list of all resources that need redistributed from inactive buckets
            var resourcesToRedistribute = new List<TResource>();
            foreach (TBucket inactiveBucket in inactiveBuckets)
            {
                resourcesToRedistribute.AddRange(idealDistribution[inactiveBucket]);
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
        /// - Must distribute resources evenly regardles off order of inputs.
        /// </summary>
        /// <param name="buckets">Buckets among which to destribute resources.</param>
        /// <param name="resources">Resources to be distributed.</param>
        /// <returns>Dictionary of resources evenly distributed among the buckets</returns>
        private Dictionary<TBucket, List<TResource>> BuildIdealDistribution(IEnumerable<TBucket> buckets, IEnumerable<TResource> resources)
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
            var idealResourceCountPerBucket = (int)Math.Ceiling((double)resourceList.Count / bucketList.Count);
            List<TResource>.Enumerator resourceEnumerator = resourceList.GetEnumerator();
            foreach (TBucket bucket in bucketList)
            {
                var bucketResources = new List<TResource>();
                idealDistribution.Add(bucket, bucketResources);
                while (resourceEnumerator.MoveNext())
                {
                    bucketResources.Add(resourceEnumerator.Current);
                    if (bucketResources.Count >= idealResourceCountPerBucket)
                    {
                        break;
                    }
                }
            }
            return idealDistribution;
        }
    }
}
