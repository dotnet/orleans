using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans;
using TestGrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Runtime;

namespace EventSourcing.Tests
{
    [Collection("EventSourcingCluster"), TestCategory("EventSourcing"), TestCategory("Functional")]
    public class AccountGrainTests
    {
        private readonly EventSourcingClusterFixture fixture;

        public AccountGrainTests(EventSourcingClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        public async Task TestSequence(IAccountGrain account, bool hasLogStored)
        {
            Assert.Equal(0u, await account.Balance());

            var initialdepositguid = Guid.NewGuid();
            await account.Deposit(100, initialdepositguid, "initial deposit");

            Assert.Equal(100u, await account.Balance());

            var firstwithdrawalguid = Guid.NewGuid();
            var success = await account.Withdraw(70, firstwithdrawalguid, "first withdrawal");

            Assert.True(success);
            Assert.Equal(30u, await account.Balance());

            var secondwithdrawalguid = Guid.NewGuid();
            success = await account.Withdraw(70, secondwithdrawalguid, "second withdrawal");

            Assert.False(success);
            Assert.Equal(30u, await account.Balance());

            if (hasLogStored)
            {
                // check the transaction log
                var log = await account.GetTransactionLog();

                Assert.Equal(2, log.Count());
                Assert.Equal(initialdepositguid, log[0].Guid);
                Assert.Equal("initial deposit", log[0].Description);
                Assert.Equal(firstwithdrawalguid, log[1].Guid);
                Assert.Equal("first withdrawal", log[1].Description);
                Assert.True(log[0].IssueTime < log[1].IssueTime);
            }
            else
            {
                await Assert.ThrowsAsync(typeof(NotSupportedException), 
                    async () => await account.GetTransactionLog());
            }
        }

        [Fact]
        public async Task AccountOnEventStorage()
        {
            var account = this.fixture.GrainFactory.GetGrain<IAccountGrain>($"Account-{Guid.NewGuid()}", "TestGrains.AccountGrain");
            await TestSequence(account, true);
        }


        [Fact]
        public async Task AccountOnLogStorage()
        {
            var account = this.fixture.GrainFactory.GetGrain<IAccountGrain>($"Account-{Guid.NewGuid()}", "TestGrains.AccountGrain_LogStorage");
            await TestSequence(account, true);
        }

        [Fact]
        public async Task AccountOnStateStorage()
        {
            var account = this.fixture.GrainFactory.GetGrain<IAccountGrain>($"Account-{Guid.NewGuid()}", "TestGrains.AccountGrain_StateStorage");
            await TestSequence(account, false);
        }



    }
}