
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public abstract class TransactionAttributionTestRunner
    {
        protected readonly IGrainFactory grainFactory;
        protected readonly ITestOutputHelper output;

        protected TransactionAttributionTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
        {
            this.output = output;
            this.grainFactory = grainFactory;
        }

        [Fact]
        public async Task FanoutToAllAttributesTest()
        {
            ITransactionAttributionGrain root = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid());
            Dictionary<int, List<ITransactionAttributionGrain>> tiers = new Dictionary<int, List<ITransactionAttributionGrain>>
            {
                [1] = new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid()),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.NotSupported),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Required),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.RequiresNew)
                    })
            };
            Dictionary<int, List<string>> results = await root.GetNestedTransactionIds(0, tiers);
            foreach(KeyValuePair<int, List<string>> kvp in results)
            {
                this.output.WriteLine($"{kvp.Key} => {string.Join(",", kvp.Value)}");
            }
            // make sure there are 2 tiers
            Assert.Equal(2, results.Count);

            // make sure tier 0 has no transactionId
            Assert.True(results.TryGetValue(0, out List<string> tier_0));
            Assert.Single(tier_0);
            Assert.Null(tier_0.First());

            // check tier 1 transactionIds, should be null, null, guid1, guid2
            Assert.True(results.TryGetValue(1, out List<string> tier_1));
            Assert.Equal(4, tier_1.Count);
            Assert.Null(tier_1[0]);
            Assert.Null(tier_1[1]);
            Assert.NotNull(tier_1[2]);
            Assert.NotNull(tier_1[3]);
            // make sure the transaction id's differ
            Assert.NotEqual(tier_1[2], tier_1[3]);
        }

        [Fact]
        public async Task FanoutToAllAttributesFromTransactionTest()
        {
            ITransactionAttributionGrain root = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.RequiresNew);
            Dictionary<int, List<ITransactionAttributionGrain>> tiers = new Dictionary<int, List<ITransactionAttributionGrain>>
            {
                [1] = new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid()),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.NotSupported),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Required),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.RequiresNew)
                    })
            };
            Dictionary<int, List<string>> results = await root.GetNestedTransactionIds(0, tiers);
            foreach (KeyValuePair<int, List<string>> kvp in results)
            {
                this.output.WriteLine($"{kvp.Key} => {string.Join(",", kvp.Value)}");
            }
            // make sure there are 2 tiers
            Assert.Equal(2, results.Count);

            // make sure tier 0 has a transactionId
            Assert.True(results.TryGetValue(0, out List<string> tier_0));
            Assert.Single(tier_0);
            Assert.NotNull(tier_0.First());

            // check tier 1 transactionIds, should be null, null, guid1, guid2
            Assert.True(results.TryGetValue(1, out List<string> tier_1));
            Assert.Equal(4, tier_1.Count);
            Assert.Null(tier_1[0]);
            Assert.Null(tier_1[1]);
            Assert.NotNull(tier_1[2]);
            Assert.NotNull(tier_1[3]);
            // make sure required attributed transactionId matches parent
            Assert.Equal(tier_1[2], tier_0.First());
            // make sure the transaction id's differ
            Assert.NotEqual(tier_1[2], tier_1[3]);
        }

        [Fact]
        public async Task FanoutToAllRequiredTest()
        {
            ITransactionAttributionGrain root = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Required);
            Dictionary<int, List<ITransactionAttributionGrain>> tiers = new Dictionary<int, List<ITransactionAttributionGrain>>
            {
                [1] = new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Required),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Required),
                    })
            };
            Dictionary<int, List<string>> results = await root.GetNestedTransactionIds(0, tiers);
            foreach (KeyValuePair<int, List<string>> kvp in results)
            {
                this.output.WriteLine($"{kvp.Key} => {string.Join(",", kvp.Value)}");
            }
            // make sure there are 2 tiers
            Assert.Equal(2, results.Count);

            // make sure tier 0 has a transactionId
            Assert.True(results.TryGetValue(0, out List<string> tier_0));
            Assert.Single(tier_0);
            Assert.NotNull(tier_0.First());

            // check tier 1 transactionIds, should be null, null, guid1, guid2
            Assert.True(results.TryGetValue(1, out List<string> tier_1));
            Assert.Equal(2, tier_1.Count);
            // make sure required attributed transactionId matches parent
            Assert.Equal(tier_1[0], tier_0.First());
            // make sure required attributed transactionId matches parent
            Assert.Equal(tier_1[1], tier_0.First());
        }

        [Fact]
        public async Task FanoutToAllRequiresNewTest()
        {
            ITransactionAttributionGrain root = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.RequiresNew);
            Dictionary<int, List<ITransactionAttributionGrain>> tiers = new Dictionary<int, List<ITransactionAttributionGrain>>
            {
                [1] = new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.RequiresNew),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.RequiresNew),
                    })
            };
            Dictionary<int, List<string>> results = await root.GetNestedTransactionIds(0, tiers);
            foreach (KeyValuePair<int, List<string>> kvp in results)
            {
                this.output.WriteLine($"{kvp.Key} => {string.Join(",", kvp.Value)}");
            }
            // make sure there are 2 tiers
            Assert.Equal(2, results.Count);

            // make sure tier 0 has a transactionId
            Assert.True(results.TryGetValue(0, out List<string> tier_0));
            Assert.Single(tier_0);
            Assert.NotNull(tier_0.First());

            // check tier 1 transactionIds, should be null, null, guid1, guid2
            Assert.True(results.TryGetValue(1, out List<string> tier_1));
            Assert.Equal(2, tier_1.Count);
            // make sure RequiresNew attributed transactionId does not matches parent
            Assert.NotEqual(tier_1[0], tier_0.First());
            // make sure RequiresNew attributed transactionId does not matches parent
            Assert.NotEqual(tier_1[1], tier_0.First());
            // make sure the transaction id's differ
            Assert.NotEqual(tier_1[0], tier_1[1]);
        }
    }
}
