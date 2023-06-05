using Orleans.Transactions.TestKit;
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

        /// <summary>
        /// Test all attributes that can be called from a non transactional call.  Ensure they all behave correctly.
        /// These include:
        /// No attribute - should have no transaction context
        /// Suppress - should have no transaction context
        /// CreateOrJoin - should have unique transaction context
        /// Create - should have unique transaction context
        /// Supported - should have no transaction context
        /// NotAllowed - should have no transaction context
        /// </summary>
        [Fact]
        public async Task AllSupportedAttributesFromOutsideTransactionTest()
        {
            ITransactionAttributionGrain top = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid());
            List<ITransactionAttributionGrain>[] tiers = 
            {
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid()),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Suppress),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.CreateOrJoin),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Supported),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.NotAllowed)
                    })
            };

            List<string>[] results = await top.GetNestedTransactionIds(0, tiers);
            for(int i=0; i<results.Length; i++)
            {
                this.output.WriteLine($"{i} => {string.Join(",", results[i])}");
            }

            // make sure there are 2 tiers
            Assert.Equal(2, results.Length);

            // make sure top level call has no transactionId
            List<string> topTransactionIds = results[0];
            Assert.Single(topTransactionIds);
            Assert.Null(topTransactionIds.First());

            // check sub call transactionIds, should be null, null, guid1, guid2, null, null
            List<string> subcallTransactionIds = results[1];
            Assert.Equal(6, subcallTransactionIds.Count);

            Assert.Null(subcallTransactionIds[0]); // no attribute

            Assert.Null(subcallTransactionIds[1]); // Suppress attribute

            Assert.NotNull(subcallTransactionIds[2]); // CreateOrJoin attribute

            Assert.NotNull(subcallTransactionIds[3]); // Create new attribute
            // make sure the transaction id's for the Required and RequiredNew calls differ
            Assert.NotEqual(subcallTransactionIds[2], subcallTransactionIds[3]);

            Assert.Null(subcallTransactionIds[4]); // Supported attribute

            Assert.Null(subcallTransactionIds[5]); // NotAllowed attribute
        }

        /// <summary>
        /// Test all attributes that can be called from within a transactional call.  Ensure they all behave correctly.
        /// These include:
        /// No attribute - should have no transaction context
        /// Suppress - should have no transaction context
        /// CreateOrJoin - should have same transaction context as parent
        /// Create - should have unique transaction context
        /// Join - should have same transaction context as parent
        /// Supported - should have same transaction context as parent
        /// </summary>
        [Fact]
        public async Task AllSupportedAttributesFromWithinTransactionTest()
        {
            ITransactionAttributionGrain top = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create);
            List<ITransactionAttributionGrain>[] tiers =
            {
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid()),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Suppress),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.CreateOrJoin),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Join),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Supported)
                    })
            };

            List<string>[] results = await top.GetNestedTransactionIds(0, tiers);
            for (int i = 0; i < results.Length; i++)
            {
                this.output.WriteLine($"{i} => {string.Join(",", results[i])}");
            }

            // make sure there are 2 tiers
            Assert.Equal(2, results.Length);

            // make sure top level call has transactionId
            List<string> topTransactionIds = results[0];
            Assert.Single(topTransactionIds);
            Assert.NotNull(topTransactionIds.First());

            // check sub call transactionIds, should be null, null, guid1, guid2, where guid1 should match Id from top
            List<string> subcallTransactionIds = results[1];
            Assert.Equal(6, subcallTransactionIds.Count);

            Assert.Null(subcallTransactionIds[0]); // no attribute

            Assert.Null(subcallTransactionIds[1]); // Suppress supported attribute

            Assert.NotNull(subcallTransactionIds[2]); // CreateOrJoin attribute
            // make sure required attributed transactionId matches top
            Assert.Equal(subcallTransactionIds[2], topTransactionIds.First());

            Assert.NotNull(subcallTransactionIds[3]); // Create attribute
            // make sure the transaction id's differ
            Assert.NotEqual(subcallTransactionIds[2], subcallTransactionIds[3]);

            Assert.NotNull(subcallTransactionIds[4]); // Join attribute
            // make sure Join attributed transactionId matches top
            Assert.Equal(subcallTransactionIds[4], topTransactionIds.First());

            Assert.NotNull(subcallTransactionIds[5]); // Supported attribute
            // make sure Supported attributed transactionId matches top
            Assert.Equal(subcallTransactionIds[5], topTransactionIds.First());
        }

        [Fact]
        public async Task CreateOrJoinTest()
        {
            ITransactionAttributionGrain top = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.CreateOrJoin);
            List<ITransactionAttributionGrain>[] tiers =
            {
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.CreateOrJoin),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.CreateOrJoin),
                    })
            };

            List<string>[] results = await top.GetNestedTransactionIds(0, tiers);
            for (int i = 0; i < results.Length; i++)
            {
                this.output.WriteLine($"{i} => {string.Join(",", results[i])}");
            }

            // make sure there are 2 tiers
            Assert.Equal(2, results.Length);

            // make sure top level call has transactionId
            List<string> topTransactionIds = results[0];
            Assert.Single(topTransactionIds);
            Assert.NotNull(topTransactionIds.First());

            // check sub call transactionIds, should be null, null, guid1, guid1, where guid1 should match Id from top
            List<string> subcallTransactionIds = results[1];
            Assert.Equal(2, subcallTransactionIds.Count);
            // make sure required attributed transactionId matches parent
            Assert.Equal(subcallTransactionIds[0], topTransactionIds.First());
            // make sure required attributed transactionId matches parent
            Assert.Equal(subcallTransactionIds[1], topTransactionIds.First());
        }

        [Fact]
        public async Task CreateTest()
        {
            ITransactionAttributionGrain top = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create);
            List<ITransactionAttributionGrain>[] tiers =
            {
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create),
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create),
                    })
            };

            List<string>[] results = await top.GetNestedTransactionIds(0, tiers);
            for (int i = 0; i < results.Length; i++)
            {
                this.output.WriteLine($"{i} => {string.Join(",", results[i])}");
            }

            // make sure there are 2 tiers
            Assert.Equal(2, results.Length);

            // make sure top level call has transactionId
            List<string> topTransactionIds = results[0];
            Assert.Single(topTransactionIds);
            Assert.NotNull(topTransactionIds.First());

            // check sub call transactionIds, should be null, null, guid1, guid2, where guid1 and guid2 should be different and not match top level transactionId
            List<string> subcallTransactionIds = results[1];
            Assert.Equal(2, subcallTransactionIds.Count);
            // make sure RequiresNew attributed transactionId does not match parents
            Assert.NotEqual(subcallTransactionIds[0], topTransactionIds.First());
            // make sure RequiresNew attributed transactionId does not match parents
            Assert.NotEqual(subcallTransactionIds[1], topTransactionIds.First());
            // make sure the transaction id's differ
            Assert.NotEqual(subcallTransactionIds[0], subcallTransactionIds[1]);
        }

        /// <summary>
        /// Check to see that a transaction taken place in a call marked 'supported' can join the parent transaction
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task SupportedTest()
        {
            ITransactionAttributionGrain top = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create);
            List<ITransactionAttributionGrain>[] tiers =
            {
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Supported),
                    }),
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.CreateOrJoin),
                    })
            };

            List<string>[] results = await top.GetNestedTransactionIds(0, tiers);
            for (int i = 0; i < results.Length; i++)
            {
                this.output.WriteLine($"{i} => {string.Join(",", results[i])}");
            }

            // make sure there are 3 tiers
            Assert.Equal(3, results.Length);

            // make sure top level call has transactionId
            List<string> topTransactionIds = results[0];
            Assert.Single(topTransactionIds);
            Assert.NotNull(topTransactionIds.First());

            // check first tier call transactionIds, should be a guid which matches the parent
            List<string> tier1callTransactionIds = results[1];
            Assert.Single(tier1callTransactionIds);
            // make sure Supported attributed transactionId matchs parents
            Assert.Equal(tier1callTransactionIds[0], topTransactionIds.First());

            // check second tier call transactionIds, should be a guid which matches the parent
            List<string> tier2callTransactionIds = results[2];
            Assert.Single(tier2callTransactionIds);
            // make sure CreateOrJoin attributed transactionId matchs parents
            Assert.Equal(tier2callTransactionIds[0], topTransactionIds.First());
        }

        [Fact]
        public async Task JoinFailTest()
        {
            ITransactionAttributionGrain fail = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid());
            List<ITransactionAttributionGrain>[] tiers =
            {
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Join),
                    })
            };

            await Assert.ThrowsAsync<NotSupportedException>(() => fail.GetNestedTransactionIds(0, tiers));
        }

        [Fact]
        public async Task NotAllowedFailTest()
        {
            ITransactionAttributionGrain fail = this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.Create);
            List<ITransactionAttributionGrain>[] tiers =
            {
                new List<ITransactionAttributionGrain>(new[] {
                    this.grainFactory.GetTransactionAttributionGrain(Guid.NewGuid(), TransactionOption.NotAllowed),
                    })
            };

            OrleansTransactionAbortedException exception = await Assert.ThrowsAsync<OrleansTransactionAbortedException>(() => fail.GetNestedTransactionIds(0, tiers));
            Assert.IsType<NotSupportedException>(exception.InnerException);
        }
    }
}
