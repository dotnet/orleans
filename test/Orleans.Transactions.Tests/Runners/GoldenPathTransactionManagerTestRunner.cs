using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Orleans.Transactions.Abstractions;
using System.Diagnostics;

namespace Orleans.Transactions.Tests
{
    public class GoldenPathTransactionManagerTestRunner : IDisposable
    {
        private readonly TimeSpan logMaintenanceInterval;
        private readonly TimeSpan storageDelay;
        private readonly ITestOutputHelper output;
        private readonly ITransactionManager transactionManager;

        protected GoldenPathTransactionManagerTestRunner(ITransactionManager transactionManager, TimeSpan logMaintenanceInterval, TimeSpan storageDelay, ITestOutputHelper output)
        {
            this.transactionManager = transactionManager;
            this.logMaintenanceInterval = logMaintenanceInterval;
            this.storageDelay = storageDelay;
            this.output = output;
        }

        [SkippableFact]
        public async Task StartCommitTransaction()
        {
            long id = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.storageDelay.Ticks*2));
            var info = new TransactionInfo(id);
            this.transactionManager.CommitTransaction(info);
            await WaitForTransactionCommit(id, this.logMaintenanceInterval + this.storageDelay);
        }

        [SkippableFact(Skip = "Intermittent failure, jbragg investigating")]
        public async Task TransactionTimeout()
        {
            long id = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.logMaintenanceInterval.Ticks / 2));
            await Task.Delay(logMaintenanceInterval);
            await Assert.ThrowsAsync<OrleansTransactionTimeoutException>(() => WaitForTransactionCommit(id, this.logMaintenanceInterval + this.storageDelay));
        }

        [SkippableFact(Skip = "Intermittent failure, jbragg investigating")]
        public async Task DependentTransaction()
        {
            long id1 = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.storageDelay.Ticks * 2));
            long id2 = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.storageDelay.Ticks * 4));

            // commit transaction
            var info = new TransactionInfo(id1);
            Stopwatch sw = Stopwatch.StartNew();
            this.transactionManager.CommitTransaction(info);
            sw.Stop();
            this.output.WriteLine($"Transaction {info} took {sw.ElapsedMilliseconds}ms to commit.");

            // resolve transaction
            sw = Stopwatch.StartNew();
            await WaitForTransactionCommit(id1, this.logMaintenanceInterval + this.storageDelay);
            sw.Stop();
            this.output.WriteLine($"Transaction {id1} took {sw.ElapsedMilliseconds}ms to resolve.");

            // commit dependent transaction
            var info2 = new TransactionInfo(id2);
            info2.DependentTransactions.Add(id1);
            sw = Stopwatch.StartNew();
            this.transactionManager.CommitTransaction(info2);
            sw.Stop();
            this.output.WriteLine($"Transaction {info2} took {sw.ElapsedMilliseconds}ms to commit.");

            // resolve dependent transaction
            sw = Stopwatch.StartNew();
            await WaitForTransactionCommit(id2, this.logMaintenanceInterval + this.storageDelay);
            sw.Stop();
            this.output.WriteLine($"Transaction {id2} took {sw.ElapsedMilliseconds}ms to resolve.");
        }

        [SkippableFact]
        public async Task OutOfOrderCommitTransaction()
        {
            long id1 = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.storageDelay.Ticks * 2));
            long id2 = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.storageDelay.Ticks * 4));

            var info2 = new TransactionInfo(id2);
            info2.DependentTransactions.Add(id1);

            this.transactionManager.CommitTransaction(info2);
            OrleansTransactionAbortedException e;
            Assert.True(this.transactionManager.GetTransactionStatus(id2, out e) == TransactionStatus.InProgress);

            var info = new TransactionInfo(id1);
            this.transactionManager.CommitTransaction(info);

            await WaitForTransactionCommit(id2, this.logMaintenanceInterval + this.storageDelay);
        }

        [SkippableFact]
        public async Task CascadingAbortTransaction()
        {
            long id1 = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.storageDelay.Ticks * 2));
            long id2 = this.transactionManager.StartTransaction(TimeSpan.FromTicks(this.storageDelay.Ticks * 4));

            var info2 = new TransactionInfo(id2);
            info2.DependentTransactions.Add(id1);

            this.transactionManager.CommitTransaction(info2);
            OrleansTransactionAbortedException abort;
            Assert.True(this.transactionManager.GetTransactionStatus(id2, out abort) == TransactionStatus.InProgress);

            this.transactionManager.AbortTransaction(id1, new OrleansTransactionAbortedException(id1.ToString()));

            var e = await Assert.ThrowsAsync<OrleansCascadingAbortException>(() => WaitForTransactionCommit(id2, this.logMaintenanceInterval + this.storageDelay));
            Assert.True(e.TransactionId == id2.ToString());
            Assert.True(e.DependentTransactionId == id1.ToString());
        }

        private async Task WaitForTransactionCommit(long transactionId, TimeSpan timeout)
        {
            var endTime = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < endTime)
            {
                OrleansTransactionAbortedException e;
                var result = this.transactionManager.GetTransactionStatus(transactionId, out e);
                switch (result)
                {
                    case TransactionStatus.Committed:
                        return;
                    case TransactionStatus.Aborted:
                        throw e;
                    case TransactionStatus.Unknown:
                        throw new OrleansTransactionInDoubtException(transactionId.ToString());
                    default:
                        Assert.True(result == TransactionStatus.InProgress);
                        await Task.Delay(logMaintenanceInterval);
                        break;
                }
            }

            throw new TimeoutException("Timed out waiting for the transaction to complete");
        }

        public void Dispose()
        {
            (this.transactionManager as IDisposable)?.Dispose();
        }
    }
}
