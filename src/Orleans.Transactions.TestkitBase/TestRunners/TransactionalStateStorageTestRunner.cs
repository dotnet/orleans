using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    public abstract class TransactionalStateStorageTestRunner<TState> : TransactionTestRunnerBase
        where TState : class, ITestState, new()
    {
        protected Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory;
        protected Func<TState> stateFactory;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="stateStorageFactory">factory to create ITransactionalStateStorage, the test runner are assuming the state 
        /// in storage is empty when ITransactionalStateStorage was created </param>
        /// <param name="stateFactory">factory to create TState for test</param>
        /// <param name="grainFactory">grain Factory needed for test runner</param>
        /// <param name="testOutput">test output to helpful messages</param>
        protected TransactionalStateStorageTestRunner(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory, Func<TState> stateFactory, 
            IGrainFactory grainFactory, Action<string> testOutput)
            :base(grainFactory, testOutput)
        {
            this.stateStorageFactory = stateStorageFactory;
            this.stateFactory = stateFactory;
        }

        public virtual async Task FirstTime_Load_ShouldReturnEmptyLoadResponse()
        {
            var stateStorage = await this.stateStorageFactory();
            var response = await stateStorage.Load();
            var defaultStateValue = new TState().state;

            //Assertion
            response.Should().NotBeNull();
            response.ETag.Should().BeNull();
            response.CommittedSequenceId.Should().Be(0);
            response.CommittedState.state.Should().Be(defaultStateValue);
            response.PendingStates.Should().BeEmpty();
        }

        private static List<PendingTransactionState<TState>> emptyPendingStates = new List<PendingTransactionState<TState>>();
  
        public virtual async Task StoreWithoutChanges()
        {
            var stateStorage = await this.stateStorageFactory();

            // load first time
            var loadresponse = await stateStorage.Load();

            // store without any changes
            var etag1 = await stateStorage.Store(loadresponse.ETag, loadresponse.Metadata, emptyPendingStates, null, null);

            // load again
            loadresponse = await stateStorage.Load();
            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Should().BeEmpty();
            loadresponse.ETag.Should().Be(etag1);
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Should().BeEmpty();

            // update metadata, then write back
            var now = DateTime.UtcNow;
            var cr = MakeCommitRecords(2, 2);
            var metadata = new TransactionalStateMetaData() { TimeStamp = now, CommitRecords = cr };
            var etag2 = await stateStorage.Store(etag1, metadata, emptyPendingStates, null, null);

            // load again, check content
            loadresponse = await stateStorage.Load();
            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.Metadata.TimeStamp.Should().Be(now);
            loadresponse.Metadata.CommitRecords.Count.Should().Be(cr.Count);
            loadresponse.ETag.Should().Be(etag2);
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Should().BeEmpty();
        }

        public virtual async Task WrongEtags()
        {
            var stateStorage = await this.stateStorageFactory();

            // load first time
            var loadresponse = await stateStorage.Load();

            // store with wrong e-tag, must fail
            try
            {
                var etag1 = await stateStorage.Store("wrong-etag", loadresponse.Metadata, emptyPendingStates, null, null);
                throw new Exception("storage did not catch e-tag mismatch");
            }
            catch (Exception) { }

            // load again
            loadresponse = await stateStorage.Load();
            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Should().BeEmpty();
            loadresponse.ETag.Should().BeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Should().BeEmpty();

            // update timestamp in metadata, then write back with correct e-tag
            var now = DateTime.UtcNow;
            var cr = MakeCommitRecords(2,2);
            var metadata = new TransactionalStateMetaData() { TimeStamp = now, CommitRecords = cr };
            var etag2 = await stateStorage.Store(null, metadata, emptyPendingStates, null, null);

            // update timestamp in metadata, then write back with wrong e-tag, must fail
            try
            {
                var now2 = DateTime.UtcNow;
                var metadata2 = new TransactionalStateMetaData() { TimeStamp = now2, CommitRecords = MakeCommitRecords(3,3) };
                await stateStorage.Store(null, metadata, emptyPendingStates, null, null);
                throw new Exception("storage did not catch e-tag mismatch");
            }
            catch (Exception) { }

            // load again, check content
            loadresponse = await stateStorage.Load();
            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.Metadata.TimeStamp.Should().Be(now);
            loadresponse.Metadata.CommitRecords.Count.Should().Be(cr.Count);
            loadresponse.ETag.Should().Be(etag2);
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Should().BeEmpty();
        }

        private PendingTransactionState<TState> MakePendingState(long seqno, int val, bool tm)
        {
            var result = new PendingTransactionState<TState>()
            {
                SequenceId = seqno,
                TimeStamp = DateTime.UtcNow,
                TransactionId = Guid.NewGuid().ToString(),
                TransactionManager = tm ? default(ParticipantId) : MakeParticipantId(),
                State = new TState()
            };
            result.State.state = val;
            return result;
        }

        private ParticipantId MakeParticipantId()
        {
            return new ParticipantId(
                                    "tm",
                                    null,
                                    // (GrainReference) grainFactory.GetGrain<ITransactionTestGrain>(Guid.NewGuid(), TransactionTestConstants.SingleStateTransactionalGrain),
                                    ParticipantId.Role.Resource | ParticipantId.Role.Manager);
        }

        private Dictionary<Guid, CommitRecord> MakeCommitRecords(int count, int size)
        {
            var result = new Dictionary<Guid, CommitRecord>();
            for (int j = 0; j < size; j++)
            {
                var r = new CommitRecord()
                {
                    Timestamp = DateTime.UtcNow,
                    WriteParticipants = new List<ParticipantId>(),
                };
                for (int i = 0; i < size; i++)
                {
                    r.WriteParticipants.Add(MakeParticipantId());
                }
                result.Add(Guid.NewGuid(), r);
            }
            return result;
        }

        private async Task PrepareOne()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstate = MakePendingState(1, 123, false);
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, null, null);

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(1);
            loadresponse.PendingStates[0].SequenceId.Should().Be(1);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate.TransactionId);
            loadresponse.PendingStates[0].State.state.Should().Be(123);
        }

        public virtual async Task ConfirmOne(bool useTwoSteps)
        { 
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstate = MakePendingState(1, 123, false);

            if (useTwoSteps)
            {
                etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, null, null);
                etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);
            }
            else
            {
                etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, 1, null);
            }

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(1);
            loadresponse.PendingStates.Count.Should().Be(0);
            loadresponse.CommittedState.state.Should().Be(123);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task CancelOne()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstate = MakePendingState(1, 123, false);

            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, null, null);
            etag = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 0);

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(0);
            loadresponse.CommittedState.state.Should().Be(initialstate);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task ReplaceOne()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstate1 = MakePendingState(1, 123, false);
            var pendingstate2 = MakePendingState(1, 456, false);

            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1 }, null, null);
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate2 }, null, null);
      
            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(1);
            loadresponse.PendingStates[0].SequenceId.Should().Be(1);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate2.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate2.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate2.TransactionId);
            loadresponse.PendingStates[0].State.state.Should().Be(456);
        }


        public virtual async Task ConfirmOneAndCancelOne(bool useTwoSteps = false, bool reverseOrder = false)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstate1 = MakePendingState(1, 123, false);
            var pendingstate2 = MakePendingState(2, 456, false);

            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1, pendingstate2 }, null, null);

            if (useTwoSteps)
            {
                if (reverseOrder)
                {
                    etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);
                    etag = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 1);
                }
                else
                {
                    etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);
                    etag = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 1);
                }
            }
            else
            {
                etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, 1);
            }

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(1);
            loadresponse.PendingStates.Count.Should().Be(0);
            loadresponse.CommittedState.state.Should().Be(123);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task PrepareMany(int count)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstates = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates.Add(MakePendingState(i + 1, i * 1000, false));
            }
            etag = await stateStorage.Store(etag, metadata, pendingstates, null, null);

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(count);

            for (int i = 0; i < count; i++)
            {
                loadresponse.PendingStates[i].SequenceId.Should().Be(i+1);
                loadresponse.PendingStates[i].TimeStamp.Should().Be(pendingstates[i].TimeStamp);
                loadresponse.PendingStates[i].TransactionManager.Should().Be(pendingstates[i].TransactionManager);
                loadresponse.PendingStates[i].TransactionId.Should().Be(pendingstates[i].TransactionId);
                loadresponse.PendingStates[i].State.state.Should().Be(i*1000);
            }
        }

        public virtual async Task ConfirmMany(int count, bool useTwoSteps)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstates = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates.Add(MakePendingState(i + 1, i * 1000, false));
            }

            if (useTwoSteps)
            {
                etag = await stateStorage.Store(etag, metadata, pendingstates, null, null);
                etag = await stateStorage.Store(etag, metadata, emptyPendingStates, count, null);
            }
            else
            {
                etag = await stateStorage.Store(etag, metadata, pendingstates, count, null);
            }

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(count);
            loadresponse.PendingStates.Count.Should().Be(0);
            loadresponse.CommittedState.state.Should().Be((count - 1)*1000);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task CancelMany(int count)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstates = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates.Add(MakePendingState(i + 1, i * 1000, false));
            }

            etag = await stateStorage.Store(etag, metadata, pendingstates, null, null);
            etag = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 0);

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(0);
            loadresponse.CommittedState.state.Should().Be(initialstate);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task ReplaceMany(int count)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstates1 = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates1.Add(MakePendingState(i + 1, i * 1000 + 1, false));
            }
            var pendingstates2 = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates2.Add(MakePendingState(i + 1, i * 1000, false));
            }

            etag = await stateStorage.Store(etag, metadata, pendingstates1, null, null);
            etag = await stateStorage.Store(etag, metadata, pendingstates2, null, null);

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(count);

            for (int i = 0; i < count; i++)
            {
                loadresponse.PendingStates[i].SequenceId.Should().Be(i + 1);
                loadresponse.PendingStates[i].TimeStamp.Should().Be(pendingstates2[i].TimeStamp);
                loadresponse.PendingStates[i].TransactionManager.Should().Be(pendingstates2[i].TransactionManager);
                loadresponse.PendingStates[i].TransactionId.Should().Be(pendingstates2[i].TransactionId);
                loadresponse.PendingStates[i].State.state.Should().Be(i * 1000);
            }
        }


        public virtual async Task GrowingBatch()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstate1 = MakePendingState(1, 11, false);
            var pendingstate2 = MakePendingState(2, 22, false);
            var pendingstate3a = MakePendingState(3, 333, false);
            var pendingstate4a = MakePendingState(4, 444, false);
            var pendingstate3b = MakePendingState(3, 33, false);
            var pendingstate4b = MakePendingState(4, 44, false);
            var pendingstate5 = MakePendingState(5, 55, false);
            var pendingstate6 = MakePendingState(6, 66, false);
            var pendingstate7 = MakePendingState(7, 77, false);
            var pendingstate8 = MakePendingState(8, 88, false);
           

            // prepare 1,2,3a,4a
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1, pendingstate2, pendingstate3a, pendingstate4a}, null, null);

            // replace 3b,4b, prepare 5, 6, 7, 8 confirm 1, 2, 3b, 4b, 5, 6
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate3b, pendingstate4b, pendingstate5, pendingstate6, pendingstate7, pendingstate8 }, 6, 6);

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(6);
            loadresponse.CommittedState.state.Should().Be(66);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(2);
            loadresponse.PendingStates[0].SequenceId.Should().Be(7);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate7.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate7.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate7.TransactionId);
            loadresponse.PendingStates[0].State.state.Should().Be(77);
            loadresponse.PendingStates[1].SequenceId.Should().Be(8);
            loadresponse.PendingStates[1].TimeStamp.Should().Be(pendingstate8.TimeStamp);
            loadresponse.PendingStates[1].TransactionManager.Should().Be(pendingstate8.TransactionManager);
            loadresponse.PendingStates[1].TransactionId.Should().Be(pendingstate8.TransactionId);
            loadresponse.PendingStates[1].State.state.Should().Be(88);
        }

        public virtual async Task ShrinkingBatch()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState.state;

            var pendingstate1 = MakePendingState(1, 11, false);
            var pendingstate2 = MakePendingState(2, 22, false);
            var pendingstate3a = MakePendingState(3, 333, false);
            var pendingstate4a = MakePendingState(4, 444, false);
            var pendingstate5 = MakePendingState(5, 55, false);
            var pendingstate6 = MakePendingState(6, 66, false);
            var pendingstate7 = MakePendingState(7, 77, false);
            var pendingstate8 = MakePendingState(8, 88, false);
            var pendingstate3b = MakePendingState(3, 33, false);
            var pendingstate4b = MakePendingState(4, 44, false);


            // prepare 1,2,3a,4a, 5, 6, 7, 8
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1, pendingstate2, pendingstate3a, pendingstate4a, pendingstate5, pendingstate6, pendingstate7, pendingstate8 }, null, null);

            // replace 3b,4b, confirm 1, 2, 3b, cancel 5, 6, 7, 8
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate3b, pendingstate4b }, 3, 4);

            loadresponse = await stateStorage.Load();
            etag = loadresponse.ETag;
            metadata = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(3);
            loadresponse.CommittedState.state.Should().Be(33);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(1);
            loadresponse.PendingStates[0].SequenceId.Should().Be(4);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate4b.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate4b.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate4b.TransactionId);
            loadresponse.PendingStates[0].State.state.Should().Be(44);
        }
        
    }
}
