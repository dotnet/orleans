using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class BestFitBalancerTests
    {
        [Fact, TestCategory("Functional")]
        public void IdealCaseMoreResourcesThanBucketsTest()
        {
            const int resourceCount = 99;
            const int bucketCount = 10;
            var idealBalance = (int)Math.Floor((double) (resourceCount)/bucketCount);
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResults, idealBalance);
        }

        [Fact, TestCategory("Functional")]
        public void IdealCaseMoreResourcesThanBuckets2Test()
        {
            const int resourceCount = 100;
            const int bucketCount = 30;
            var idealBalance = (int)Math.Floor((double)(resourceCount) / bucketCount);
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResults, idealBalance);
        }

        [Fact, TestCategory("Functional")]
        public void IdealCaseLessResourcesThanBucketsTest()
        {
            const int bucketCount = 99;
            const int resourceCount = 10;
            var idealBalance = (int)Math.Floor((double)(resourceCount) / bucketCount);
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResults, idealBalance);
        }

        [Fact, TestCategory("Functional")]
        public void IdealCaseLessResourcesThanBuckets2Test()
        {
            const int bucketCount = 100;
            const int resourceCount = 30;
            var idealBalance = (int)Math.Floor((double)(resourceCount) / bucketCount);
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResults, idealBalance);
        }

        [Fact, TestCategory("Functional")]
        public void IdealCaseResourcesMatchBucketsTest()
        {
            const int bucketCount = 100;
            const int resourceCount = 100;
            var idealBalance = (int)Math.Floor((double)(resourceCount) / bucketCount);
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResults, idealBalance);
        }

        [Fact, TestCategory("Functional")]
        public void IdealCaseResourcesDevisibleByBucketsTest()
        {
            const int resourceCount = 100;
            const int bucketCount = 10;
            var idealBalance = (int)Math.Floor((double)(resourceCount) / bucketCount);
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResults, idealBalance);
        }

        [Fact, TestCategory("Functional")]
        public void IdealCaseRangedTest()
        {
            const int MaxResourceCount = 20;
            const int MaxBucketCount = 20;

            for (int resourceCount = 1; resourceCount <= MaxResourceCount; resourceCount++)
            {
                for (int bucketCount = 1; bucketCount <= MaxBucketCount; bucketCount++)
                {
                    var idealBalance = (int)Math.Floor((double)(resourceCount) / bucketCount);
                    List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
                    List<int> resources = Enumerable.Range(0, resourceCount).ToList();
                    var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
                    Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
                    ValidateBalance(buckets, resources, balancerResults, idealBalance);
                }
            }
        }

        [Fact, TestCategory("Functional")]
        public void HalfBucketsActiveTest()
        {
            const int resourceCount = 99;
            const int bucketCount = 10;
            const int activeBucketCount = bucketCount/2;
            var idealBalance = (int)Math.Floor((double)(resourceCount) / activeBucketCount);
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            List<int> activeBuckets = buckets.Take(activeBucketCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(activeBuckets);
            ValidateBalance(activeBuckets, resources, balancerResults, idealBalance);
        }

        [Fact, TestCategory("Functional")]
        public void OrderIrrelevantTest()
        {
            const int resourceCount = 99;
            const int bucketCount = 10;
            var idealBalance = (int)Math.Floor((double)(resourceCount) / bucketCount);
            List<int> resources = Enumerable.Range(0, resourceCount).ToList();
            List<int> buckets = Enumerable.Range(0, bucketCount).ToList();
            var resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResults = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResults, idealBalance);

            // reverse inputs
            resources.Reverse();
            buckets.Reverse();
            resourceBalancer = new BestFitBalancer<int, int>(buckets, resources);
            Dictionary<int, List<int>> balancerResultsFromReversedInputs = resourceBalancer.GetDistribution(buckets);
            ValidateBalance(buckets, resources, balancerResultsFromReversedInputs, idealBalance);

            Assert.True(balancerResults.Keys.SequenceEqual(balancerResultsFromReversedInputs.Keys), "Bucket order");
            foreach (int bucket in balancerResults.Keys)
            {
                Assert.True(balancerResults[bucket].SequenceEqual(balancerResultsFromReversedInputs[bucket]), "Resource order");
            }
        }

        private void ValidateBalance(List<int> buckets, List<int> resources, Dictionary<int, List<int>> balancerResults, int idealBalance)
        {
            var resultBuckets = new List<int>();
            var resultResources = new List<int>();
            foreach (KeyValuePair<int, List<int>> kvp in balancerResults)
            {
                Assert.False(resultBuckets.Contains(kvp.Key), "Duplicate bucket found.");
                Assert.True(buckets.Contains(kvp.Key), "Unknown bucket found.");
                resultBuckets.Add(kvp.Key);
                kvp.Value.ForEach(resource =>
                {
                    Assert.False(resultResources.Contains(resource), "Duplicate resource found.");
                    Assert.True(resources.Contains(resource), "Unknown resource found.");
                    resultResources.Add(resource);
                });
                Assert.True(idealBalance <= kvp.Value.Count, "Balance not ideal");
                Assert.True(idealBalance + 1 >= kvp.Value.Count, "Balance not ideal");
            }
            Assert.Equal(buckets.Count, resultBuckets.Count);  // "bucket counts do not match"
            Assert.Equal(resources.Count, resultResources.Count);  // "resource counts do not match"
        }
    }
}
