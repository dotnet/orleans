using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    public abstract class TransactionalStateStorageTestRunner<TState> : TransactionTestRunnerBase
        where TState : class, new()
    {
        protected Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory;
        protected Func<int, TState> stateFactory;
        protected Func<EquivalencyAssertionOptions<TState>, EquivalencyAssertionOptions<TState>> assertConfig;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="stateStorageFactory">factory to create ITransactionalStateStorage, the test runner are assuming the state 
        /// in storage is empty when ITransactionalStateStorage was created </param>
        /// <param name="stateFactory">factory to create TState for test</param>
        /// <param name="grainFactory">grain Factory needed for test runner</param>
        /// <param name="testOutput">test output to helpful messages</param>
        /// <param name="assertConfig">A reference to the FluentAssertions.Equivalency.EquivalencyAssertionOptions`1
        ///     configuration object that can be used to influence the way the object graphs
        ///     are compared</param>
        protected TransactionalStateStorageTestRunner(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory, Func<int, TState> stateFactory, 
            IGrainFactory grainFactory, Action<string> testOutput,
            Func<EquivalencyAssertionOptions<TState>, EquivalencyAssertionOptions<TState>> assertConfig = null)
            :base(grainFactory, testOutput)
        {
            this.stateStorageFactory = stateStorageFactory;
            this.stateFactory = stateFactory;
            this.assertConfig = assertConfig;
        }

        public virtual async Task FirstTime_Load_ShouldReturnEmptyLoadResponse()
        {
            var stateStorage = await this.stateStorageFactory();
            var response = await stateStorage.Load();
            var defaultStateValue = new TState();

            //Assertion
            response.Should().NotBeNull();
            response.ETag.Should().BeNull();
            response.CommittedSequenceId.Should().Be(0);
            AssertTState(response.CommittedState, defaultStateValue);
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

        private void AssertTState(TState actual, TState expected)
        {
            if(assertConfig == null)
                actual.ShouldBeEquivalentTo(expected);
            else
                actual.ShouldBeEquivalentTo(expected, assertConfig);
        }

        private PendingTransactionState<TState> MakePendingState(long seqno, TState val, bool tm)
        {
            var result = new PendingTransactionState<TState>()
            {
                SequenceId = seqno,
                TimeStamp = DateTime.UtcNow,
                TransactionId = Guid.NewGuid().ToString(),
                TransactionManager = tm ? default(ParticipantId) : MakeParticipantId(),
                State = new TState()
            };
            result.State = val;
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
            var initialstate = loadresponse.CommittedState;

            var expectedState = this.stateFactory(123);
            var pendingstate = MakePendingState(1, expectedState, false);
            _ = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, null, null);

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(1);
            loadresponse.PendingStates[0].SequenceId.Should().Be(1);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate.TransactionId);
            AssertTState(loadresponse.PendingStates[0].State, expectedState);
        }

        public virtual async Task ConfirmOne(bool useTwoSteps)
        { 
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var expectedState = this.stateFactory(123);
            var pendingstate = MakePendingState(1, expectedState, false);

            if (useTwoSteps)
            {
                etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, null, null);
                _ = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);
            }
            else
            {
                _ = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, 1, null);
            }

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(1);
            loadresponse.PendingStates.Count.Should().Be(0);
            AssertTState(loadresponse.CommittedState, expectedState);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task CancelOne()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;
            
            var pendingstate = MakePendingState(1, this.stateFactory(123), false);

            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate }, null, null);
            _ = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 0);

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(0);
            AssertTState(loadresponse.CommittedState,initialstate);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task ReplaceOne()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var expectedState1 = this.stateFactory(123);
            var expectedState2 = this.stateFactory(456);
            var pendingstate1 = MakePendingState(1, expectedState1, false);
            var pendingstate2 = MakePendingState(1, expectedState2, false);

            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1 }, null, null);
            _ = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate2 }, null, null);
      
            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(1);
            loadresponse.PendingStates[0].SequenceId.Should().Be(1);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate2.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate2.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate2.TransactionId);
            AssertTState(loadresponse.PendingStates[0].State,expectedState2);
        }


        public virtual async Task ConfirmOneAndCancelOne(bool useTwoSteps = false, bool reverseOrder = false)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var expectedState = this.stateFactory(123);
            var pendingstate1 = MakePendingState(1, expectedState, false);
            var pendingstate2 = MakePendingState(2, this.stateFactory(456), false);

            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1, pendingstate2 }, null, null);

            if (useTwoSteps)
            {
                if (reverseOrder)
                {
                    etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);
                    _ = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 1);
                }
                else
                {
                    etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);
                    _ = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 1);
                }
            }
            else
            {
                _ = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, 1);
            }

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(1);
            loadresponse.PendingStates.Count.Should().Be(0);
            AssertTState(loadresponse.CommittedState,expectedState);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task PrepareMany(int count)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var pendingstates = new List<PendingTransactionState<TState>>();
            var expectedStates = new List<TState>();
            for (int i = 0; i < count; i++)
            {
                expectedStates.Add(this.stateFactory(i * 1000));
            }

            for (int i = 0; i < count; i++)
            {
                pendingstates.Add(MakePendingState(i + 1, expectedStates[i], false));
            }
            _ = await stateStorage.Store(etag, metadata, pendingstates, null, null);

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

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
                AssertTState(loadresponse.PendingStates[i].State,expectedStates[i]);
            }
        }

        public virtual async Task ConfirmMany(int count, bool useTwoSteps)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var expectedStates = new List<TState>();
            for (int i = 0; i < count; i++)
            {
                expectedStates.Add(this.stateFactory(i * 1000));
            }
            var pendingstates = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates.Add(MakePendingState(i + 1, expectedStates[i], false));
            }

            if (useTwoSteps)
            {
                etag = await stateStorage.Store(etag, metadata, pendingstates, null, null);
                _ = await stateStorage.Store(etag, metadata, emptyPendingStates, count, null);
            }
            else
            {
                _ = await stateStorage.Store(etag, metadata, pendingstates, count, null);
            }

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(count);
            loadresponse.PendingStates.Count.Should().Be(0);
            AssertTState(loadresponse.CommittedState,expectedStates[count - 1]);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task CancelMany(int count)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var expectedStates = new List<TState>();
            for (int i = 0; i < count; i++)
            {
                expectedStates.Add(this.stateFactory(i * 1000));
            }

            var pendingstates = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates.Add(MakePendingState(i + 1, expectedStates[i], false));
            }

            etag = await stateStorage.Store(etag, metadata, pendingstates, null, null);
            _ = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 0);

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(0);
            AssertTState(loadresponse.CommittedState,initialstate);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
        }

        public virtual async Task ReplaceMany(int count)
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var expectedStates1 = new List<TState>();
            for (int i = 0; i < count; i++)
            {
                expectedStates1.Add(this.stateFactory(i * 1000 + 1));
            }

            var expectedStates2 = new List<TState>();
            for (int i = 0; i < count; i++)
            {
                expectedStates2.Add(this.stateFactory(i * 1000));
            }

            var pendingstates1 = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates1.Add(MakePendingState(i + 1, expectedStates1[i], false));
            }
            var pendingstates2 = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < count; i++)
            {
                pendingstates2.Add(MakePendingState(i + 1, expectedStates2[i], false));
            }

            etag = await stateStorage.Store(etag, metadata, pendingstates1, null, null);
            _ = await stateStorage.Store(etag, metadata, pendingstates2, null, null);

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

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
                AssertTState(loadresponse.PendingStates[i].State, expectedStates2[i]);
            }
        }


        public virtual async Task GrowingBatch()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;
            
            var pendingstate1 = MakePendingState(1, this.stateFactory(11), false);
            var pendingstate2 = MakePendingState(2, this.stateFactory(22), false);
            var pendingstate3a = MakePendingState(3, this.stateFactory(333), false);
            var pendingstate4a = MakePendingState(4, this.stateFactory(444), false);
            var pendingstate3b = MakePendingState(3, this.stateFactory(33), false);
            var pendingstate4b = MakePendingState(4, this.stateFactory(44), false);
            var pendingstate5 = MakePendingState(5, this.stateFactory(55), false);

            var expectedState6 = this.stateFactory(66);
            var pendingstate6 = MakePendingState(6, expectedState6, false);
            var expectedState7 = this.stateFactory(77);
            var pendingstate7 = MakePendingState(7, expectedState7, false);
            var expectedState8 = this.stateFactory(88);
            var pendingstate8 = MakePendingState(8, expectedState8, false);
           

            // prepare 1,2,3a,4a
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1, pendingstate2, pendingstate3a, pendingstate4a}, null, null);

            // replace 3b,4b, prepare 5, 6, 7, 8 confirm 1, 2, 3b, 4b, 5, 6
            _ = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate3b, pendingstate4b, pendingstate5, pendingstate6, pendingstate7, pendingstate8 }, 6, null);

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(6);
            AssertTState(loadresponse.CommittedState, expectedState6);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(2);
            loadresponse.PendingStates[0].SequenceId.Should().Be(7);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate7.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate7.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate7.TransactionId);
            AssertTState(loadresponse.PendingStates[0].State, expectedState7);
            loadresponse.PendingStates[1].SequenceId.Should().Be(8);
            loadresponse.PendingStates[1].TimeStamp.Should().Be(pendingstate8.TimeStamp);
            loadresponse.PendingStates[1].TransactionManager.Should().Be(pendingstate8.TransactionManager);
            loadresponse.PendingStates[1].TransactionId.Should().Be(pendingstate8.TransactionId);
            AssertTState(loadresponse.PendingStates[1].State, expectedState8);
        }

        public virtual async Task ShrinkingBatch()
        {
            var stateStorage = await this.stateStorageFactory();
            var loadresponse = await stateStorage.Load();
            var etag = loadresponse.ETag;
            var metadata = loadresponse.Metadata;
            var initialstate = loadresponse.CommittedState;

            var pendingstate1 = MakePendingState(1, this.stateFactory(11), false);
            var pendingstate2 = MakePendingState(2, this.stateFactory(22), false);
            var pendingstate3a = MakePendingState(3, this.stateFactory(333), false);
            var pendingstate4a = MakePendingState(4, this.stateFactory(444), false);
            var pendingstate5 = MakePendingState(5, this.stateFactory(55), false);
            var pendingstate6 = MakePendingState(6, this.stateFactory(66), false);
            var pendingstate7 = MakePendingState(7, this.stateFactory(77), false);
            var pendingstate8 = MakePendingState(8, this.stateFactory(88), false);
            var expectedState3b = this.stateFactory(33);
            var pendingstate3b = MakePendingState(3, expectedState3b, false);
            var expectedState4b = this.stateFactory(44);
            var pendingstate4b = MakePendingState(4, expectedState4b, false);


            // prepare 1,2,3a,4a, 5, 6, 7, 8
            etag = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate1, pendingstate2, pendingstate3a, pendingstate4a, pendingstate5, pendingstate6, pendingstate7, pendingstate8 }, null, null);

            // replace 3b,4b, confirm 1, 2, 3b, cancel 5, 6, 7, 8
            _ = await stateStorage.Store(etag, metadata, new List<PendingTransactionState<TState>>() { pendingstate3b, pendingstate4b }, 3, 4);

            loadresponse = await stateStorage.Load();
            _ = loadresponse.ETag;
            _ = loadresponse.Metadata;

            loadresponse.Should().NotBeNull();
            loadresponse.Metadata.Should().NotBeNull();
            loadresponse.CommittedSequenceId.Should().Be(3);
            AssertTState(loadresponse.CommittedState, expectedState3b);
            loadresponse.Metadata.TimeStamp.Should().Be(default(DateTime));
            loadresponse.Metadata.CommitRecords.Count.Should().Be(0);
            loadresponse.PendingStates.Count.Should().Be(1);
            loadresponse.PendingStates[0].SequenceId.Should().Be(4);
            loadresponse.PendingStates[0].TimeStamp.Should().Be(pendingstate4b.TimeStamp);
            loadresponse.PendingStates[0].TransactionManager.Should().Be(pendingstate4b.TransactionManager);
            loadresponse.PendingStates[0].TransactionId.Should().Be(pendingstate4b.TransactionId);
            AssertTState(loadresponse.PendingStates[0].State, expectedState4b);
        }
        
    }
}
