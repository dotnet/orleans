using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

using LoadTestBase;
using Orleans.Runtime;

namespace PresenceConsoleTest
{
    class UserWorker : WorkerBase
    {
        private const int UserBlockSize = 1000;
        private const int TimerInterval = 10 * 1000;
        private const int ReportSize = 1 * 1000;
        private const int AddBlockThreshold = 20;
        private const int RemoveBlockThreshold = 3;
        private Latency _aggregateLatency = new Latency();
        private readonly AutoResetEvent _autoEvent = new AutoResetEvent(false);
        private readonly Random _random = new Random();
        private readonly List<int> _runningUserBlocks = new List<int>(); 
        private readonly List<int> _stoppedUserBlocks = new List<int>();
        private readonly List<UserBlock> _userBlocks = new List<UserBlock>();
        
        private int _nInitialBlocks = 5;
        private int _randomUserTimeInterval = 1;

        private int _addBlockCount, _removeBlockCount;        
        private int _nMaxBlocks;
        private int _requestCount;
        private int _failureCount;
        private int _lateCount;
        private int _busyCount;
        
        private bool _randomBlocks;
        private bool _runComplete;
        private bool _runIndefintely;

        private Timer _randomUserChangeTimer;

        public void Initialize(int numUsers, long numRequests, int reportBlockSize, int pipelineSize, Callback callback, IPEndPoint gateway, int instanceIndex, bool useAzureSiloTable, bool warmup, int numInitialBlocks, int numMaxBlocks, int randomUserTimeInterval, bool randomBlocks = false)
        {
            _nInitialBlocks = numInitialBlocks;
            _nMaxBlocks = numMaxBlocks;
            _randomBlocks = randomBlocks;
            _randomUserTimeInterval = randomUserTimeInterval;

            if (numRequests == 0)
            {
                numRequests = numUsers;
                _runIndefintely = true;
            }

            Initialize(numUsers, numRequests, reportBlockSize, pipelineSize, callback, gateway, instanceIndex, useAzureSiloTable, warmup);
        }

        /// <summary>
        /// Run the test with the specified configuration
        /// </summary>
        /// <returns>A list of exceptions that have occurred.</returns>
        public override List<Exception> Run()
        {
            lock (this)
            {
                for (var i = 0; i < _nInitialBlocks * 10; i++) // Create 10 times the initial number of buckets so that we can randomly use buckets within this larger pool of buckets
                {
                    AddUserBlock();
                }

                for (int i = 0; i < _nInitialBlocks; i++) // Start the initial set of blocks
                {
                    StartUserBlock(i);
                    Thread.Sleep(1000);
                }
            }

            Orleans.GrainClient.Logger.Verbose("Started initial {0} blocks of {1} users each.", _nInitialBlocks, UserBlockSize);

            _randomUserChangeTimer = new Timer(RandomUserChange, _autoEvent, TimeSpan.FromMinutes(_randomUserTimeInterval), TimeSpan.FromMinutes(_randomUserTimeInterval));
            
            while (!_runComplete)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            return new List<Exception>();
        }

        private void ReportCallback(int requests, int currentFailures, int currentLate, int currentBusy, Latency blockLatency)
        {
            lock (this)
            {
                _requestCount += requests;
                _failureCount += currentFailures;
                _lateCount += currentLate;
                _busyCount += currentBusy;
                _aggregateLatency.AddLatencyReadings(blockLatency);

                if (_requestCount > 0 && _requestCount % _reportBlockSize == 0)
                {
                    _callback.ReportBlock(name, _failureCount, 0, _lateCount, _busyCount, _aggregateLatency);
                    _aggregateLatency = new Latency();

                    if (_failureCount == 0 && _lateCount == 0)
                        _addBlockCount++;
                    else
                        _removeBlockCount++;

                    if (_addBlockCount >= AddBlockThreshold)
                    {
                        _addBlockCount = 0;
                        if (_randomBlocks)
                            StartRandomUserBlock();
                        else
                            StartNextUserBlock();
                    }

                    if (_removeBlockCount >= RemoveBlockThreshold)
                    {
                        _removeBlockCount = 0;
                        if (_randomBlocks)
                            StopRandomUserBlock();
                        else
                            StopNextUserBlock();
                    }

                    _failureCount = 0;
                    _lateCount = 0;
                }

                if (_requestCount >= nRequests && !_runIndefintely)
                {
                    _runComplete = true;
                }
            }
        }

        public void StartNextUserBlock()
        {
            if (_userBlocks.Count - _stoppedUserBlocks.Count >= _nMaxBlocks && _nMaxBlocks >= 1)
            {
                Orleans.GrainClient.Logger.Verbose("The max number ({0}) of userblocks has been reached", _nMaxBlocks);
                return; // There are to many blocks it will overwhelm the system
            }

            switch (_stoppedUserBlocks.Count == 0)
            {
                case true: // If there are no stoppedUserBlocks create a new one and then run it
                    AddUserBlock();
                    StartUserBlock(_userBlocks.Count - 1);
                    break;
                case false: // If there are stopped user blocks start the first one 
                    StartUserBlock(_stoppedUserBlocks[0]);
                    break;
            }
        }

        public void StartRandomUserBlock()
        {
            int randNum = _userBlocks.Count;  // Defaulting to userblocks.count so that if there are no stopped blocks
            
            try
            {
                if (_stoppedUserBlocks.Count == 0) // If there are no stopped user blocks add a new one.
                    AddUserBlock();
                else // If there are stopped user blocks pick a random one to restart
                    randNum = _stoppedUserBlocks[_random.Next(_stoppedUserBlocks.Count)];

                StartUserBlock(randNum);
            }
            catch (ArgumentOutOfRangeException)
            {
                Orleans.GrainClient.Logger.Verbose("Argument out of range exception in StartRandomUserBlock.  Number is {0}", randNum);
            }
        }

        private void AddUserBlock()
        {
            _userBlocks.Add(new UserBlock(UserBlockSize, UserBlockSize * _userBlocks.Count, TimerInterval, ReportSize, ReportCallback));
            _stoppedUserBlocks.Add(_userBlocks.Count - 1); // adding the block to the stoppedBlocks list until it has run.
        }

        private void StartUserBlock(int position)
        {
            if (position >= _userBlocks.Count || position < 0)
            {
                Orleans.GrainClient.Logger.Verbose("StartUserBlock: Position is greater then number of items in _userBlock.  Position is {0}", position);
                return;
            }

            _userBlocks[position].Run();
            _stoppedUserBlocks.Remove(position);  // now that it is running remove it from the stopped blocks
            _runningUserBlocks.Add(position);     // now that it is running add it to the running blocks
            Orleans.GrainClient.Logger.Verbose("Started block #{0} of {1} users for a total of {2} blocks.", position, UserBlockSize, (_userBlocks.Count - _stoppedUserBlocks.Count));
        }

        /// <summary>
        /// Stops the next user block until the last user block and then restarts at 0.
        /// </summary>
        public void StopNextUserBlock()
        {
            var userBlockNumber = 0;

            if (_stoppedUserBlocks.Count == _userBlocks.Count)
            {
                Orleans.GrainClient.Logger.Verbose("All userblocks are stopped.  Cannot stop anymore.");
                return;
            }

            if (_stoppedUserBlocks.Count != 0)
            {
                // This should stop all the way to the last block and then start back at block 0
                switch ((_stoppedUserBlocks.Last() + 1) == _userBlocks.Count)
                {
                    case true:
                        userBlockNumber = 0;
                        break;
                    case false:
                        userBlockNumber = _stoppedUserBlocks.Last() + 1;
                        break;
                }
            }

            StopUserBlock(userBlockNumber);
        }

        /// <summary>
        /// Stops a random user block from the running user blocks
        /// </summary>
        public void StopRandomUserBlock()
        {
            int randNum = 0;
            try
            {
                randNum = _runningUserBlocks[_random.Next(_runningUserBlocks.Count)];
                StopUserBlock(randNum);
            }
            catch (ArgumentOutOfRangeException)
            {
                Orleans.GrainClient.Logger.Verbose("Argument out of range exception in StopRandomUserBlock.  Number is {0}", randNum);
            }
        }

        /// <summary>
        /// Stops the specified user block.
        /// </summary>
        /// <param name="position">The position in the user block array that should be stopped</param>
        private void StopUserBlock(int position)
        {
            if (position >= _userBlocks.Count || position < 0)
            {
                Orleans.GrainClient.Logger.Verbose("StopUserBlock: Position is greater then number of items in _userBlock.  Position is {0}", position);
                return;
            }

            _stoppedUserBlocks.Add(position);
            _runningUserBlocks.Remove(position);
            _userBlocks[position].Stop();
            Orleans.GrainClient.Logger.Verbose("Removed block #{0} of {1} users for a total of {2} blocks.", position,
                                      UserBlockSize, (_userBlocks.Count - _stoppedUserBlocks.Count));
        }

        private void RandomUserChange(object state)
        {
            if (_randomBlocks)
            {
                StopRandomUserBlock();
                StartRandomUserBlock();
            }
            else
            {
                StopNextUserBlock();
                StartNextUserBlock();
            }
        }

        class UserBlock
        {
            private readonly UserData[] users;
            private readonly Timer timer;
            private readonly long timerInterval;
            private readonly int reportBlockSize;
            private readonly Action<int, int, int, int, Latency> callback;
            private int requestsSinceLastReport;
            int currentLate;
            int currentBusy;
            int currentFailures;
            private int totalFailures;
            private bool stop = true;
            private Latency aggregateLatency = new Latency();

            public UserBlock(int blockSize, int firstUserId, long timerInterval, int reportBlockSize, Action<int, int, int, int, Latency> callback)
            {
                this.timerInterval = timerInterval;
                this.reportBlockSize = reportBlockSize;
                this.callback = callback;

                users = new UserData[blockSize];

                for (int i = 0; i < blockSize; i++)
                    users[i] = new UserData
                    {
                        Id = firstUserId + i,
                        FailuresTotal = 0,
                        LateTotals = 0,
                        Promise = TaskDone.Done
                    };
                timer = new Timer(TimerFunction);
            }

            public void Run()
            {
                if (stop) // putting this protection in so that only stopped have their interval changed.
                timer.Change(timerInterval, -1);
                
                stop = false;
            }

            public void Stop()
            {
                stop = true;
            }

            private void TimerFunction(object state)
            {
                if (stop)
                {
                    // timer.Dispose();
                    return;
                }

                foreach (UserData user in users)
                {
                    UserData internalUser = user;
                    TaskStatus status = user.Promise.Status;

                    if (status == TaskStatus.Running)
                    {
                        user.LateTotals = user.LateTotals + 1;
                        currentLate = currentLate + 1;
                        continue;
                    }

                    if (status == TaskStatus.Faulted)
                    {
                        totalFailures = totalFailures + 1;
                        var ex = user.Promise.Exception;
                        if (typeof(TimeoutException).IsAssignableFrom(ex.GetType()))
                        {
                            user.LateTotals = user.LateTotals + 1;
                            currentLate = currentLate + 1;
                        }
                        else if (typeof(GatewayTooBusyException).IsAssignableFrom(ex.GetType()))
                        {
                            user.BusyTotals = user.BusyTotals + 1;
                            currentBusy = currentBusy + 1;
                        }
                        else
                        {
                            user.FailuresTotal = user.FailuresTotal + 1;
                            currentFailures = currentFailures + 1;
                        }

                        user.Promise.Ignore();
                    }
                   
                    var startTime = DateTime.UtcNow;
                    
                    user.Promise = RunIteration(user.Id, user.Id + 1).ContinueWith(t =>
                    {
                        if (!t.IsFaulted)
                        {
                            long latency = (DateTime.UtcNow - startTime).Ticks;
                            aggregateLatency.AddLatencyReading(latency);
                        }
                        else
                        {
                            currentFailures = currentFailures + 1;
                            //aggregateLatency.AddLatencyReading((DateTime.Now - startTime).Ticks);
                            var ex = t.Exception.GetBaseException();
                            // failures.Add(ex);
                            string errorString = string.Format("UserNumber: {0}      Exception: {1}", internalUser.Id,
                                ex.Message);
                            Orleans.GrainClient.Logger.Verbose(errorString);
                        }
                        // TO DO might want to set a count on these exceptions
                    });

                    requestsSinceLastReport = requestsSinceLastReport + 1;

                    if (requestsSinceLastReport == reportBlockSize)
                    {
                        lock (aggregateLatency)
                        {
                            callback(requestsSinceLastReport, currentFailures, currentLate, currentBusy, aggregateLatency);
                            aggregateLatency = new Latency();
                        }
                        requestsSinceLastReport = 0;
                        currentFailures = 0;
                        currentLate = 0;
                        currentBusy = 0;
                    }
                }

                timer.Change(timerInterval, -1);
            }

            class UserData
            {
                public int Id { get; set; }

                public Task Promise { get; set; }

                public int FailuresTotal { get; set; }

                public int LateTotals { get; set; }

                public int BusyTotals { get; set; }
            }
        }
    }
}