using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LoadTestBase;
using LoadTestGrainInterfaces;

using Orleans.Runtime;

namespace NewReminderLoadTest
{
    public class Worker : OrleansClientWorkerBase
    {
        private int _startPoint;
        private Guid[] _grainGuidPool;
        private IReminderLoadTestGrain[] _grains;
        private int _nextGuidInPool;
        private ConcurrentDictionary<string, int> _reminders;
        private ConcurrentQueue<string> _queue; 
        private int _reminderPoolSize;

        // This is an example of worker initialization.
        // Pre-create grains, per-allocate data buffers, etc...
        public void ApplicationInitialize(int grainPoolSize, int reminderPoolSize, int startBarrierSize, bool shareGrains)
        {
            WriteProgressWithoutBulking(string.Format("Start ApplicationInitialize by worker {0}", Name));

            Stopwatch stopwatch = Stopwatch.StartNew();

            if (grainPoolSize < 1)
            {
                throw new ArgumentOutOfRangeException("grainPoolSize");
            }
            if (reminderPoolSize < 1)
            {
                throw new ArgumentOutOfRangeException("reminderPoolSize");
            }

            AsyncPipeline pipeline = new AsyncPipeline(250);

            grainPoolSize = (grainPoolSize > nRequests) ? (int)nRequests : grainPoolSize;

            // we need 1 guid for each stream producer and 1 guid for each request.
            InitializeGuidPool(grainPoolSize, shareGrains);

            AsyncPipeline initPipeline = new AsyncPipeline(500);
            Random rng = new Random();
            _startPoint = rng.Next(grainPoolSize);

            // we need `grainCount` guids for our grains. we get them in bulk now so that we can use a different initialization order to reduce potential contention during initialization.
            Guid[] grainGuids = new Guid[grainPoolSize];
            for (int i = 0; i < grainGuids.Length; ++i)
                grainGuids[i] = NextGuid();
            _grains = new IReminderLoadTestGrain[grainPoolSize];
            for (int i = 0; i < _grains.Length; ++i)
            {
                int guidIndex = (i + _startPoint) % _grains.Length;
                _grains[i] = ReminderLoadTestGrainFactory.GetGrain(grainGuids[guidIndex]);
                pipeline.Add(_grains[i].Noop());
            }
            pipeline.Wait();

            WriteProgressWithoutBulking("Filling reminder pool...");
            _reminders = new ConcurrentDictionary<string, int>();
            _reminderPoolSize = reminderPoolSize;
            for (int i = 0; i < reminderPoolSize; ++i)
            {
                int grainIndex = i % _grains.Length;
                string newGuid = Guid.NewGuid().ToString();
                pipeline.Add(_grains[grainIndex].RegisterReminder(newGuid));
                if (!_reminders.TryAdd(newGuid, grainIndex))
                {
                    throw new InvalidOperationException("Add to ConcurrentDictionary failed.");
                }
            }

            _queue = new ConcurrentQueue<string>();
            string[] keys = _reminders.Keys.ToArray();
            // fisher-yates shuffle to determine which order the reminders should be processed.
            for (int i = keys.Length - 1; i >= 1; --i)
            {
                int j = rng.Next(i);
                string tmp = keys[i];
                keys[i] = keys[j];
                keys[j] = tmp;
            }
            foreach (var key in keys)
            {
                _queue.Enqueue(key);
            }

            WriteProgressWithoutBulking("Waiting on pipeline to empty...");
            initPipeline.Wait();
            stopwatch.Stop();
            WriteProgressWithoutBulking(string.Format("Done ApplicationInitialize by worker {0} in {1} seconds", Name, stopwatch.Elapsed.TotalSeconds));

            WaitAtStartBarrier(startBarrierSize).Wait();
        }

        private void InitializeGuidPool(int count, bool sharePool)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException("count");

            IGuidPoolGrain pool;
            if (sharePool)
                pool = GuidPoolGrainFactory.GetGrain(0);
            else
                pool = GuidPoolGrainFactory.GetGrain(Guid.NewGuid());
            WriteProgressWithoutBulking("Worker.GetGuids: fetching {0} guids; shared={1}", count, sharePool);
            Guid[] guids = pool.GetGuids(count).Result;
            WriteProgressWithoutBulking("Worker.ApplicationInitialize: {0} guids fetched for grain pool", count);
            _grainGuidPool = guids;
            _nextGuidInPool = 0;
        }

        private Guid NextGuid()
        {
            return _grainGuidPool[_nextGuidInPool++];
        }

        protected override async Task IssueRequest(int requestNumber, int threadNumber)
        {
            try
            {
                string oldGuid;
                if (!_queue.TryDequeue(out oldGuid))
                {
                    throw new InvalidOperationException("failed to dequeue reminder name");
                }

                int grainIndex;
                if (!_reminders.TryRemove(oldGuid, out grainIndex))
                {
                    throw new KeyNotFoundException("reminder not found");
                }

                string newGuid = Guid.NewGuid().ToString();
                Task unregistered = _grains[grainIndex].UnregisterReminder(oldGuid);
                Task registered = _grains[grainIndex].RegisterReminder(newGuid);

                _reminders.TryAdd(newGuid, grainIndex);
                _queue.Enqueue(newGuid);
                await Task.WhenAll(new[] {unregistered, registered});
            }
            catch (Exception e)
            {
                WriteProgress("NewReminderLoadTest.Worker.IssueRequest: FAIL #{0} {1}", requestNumber, e.ToString());
                WriteProgress("\n\n*********************************************************\n");
                throw;
            }
        }
    }
}