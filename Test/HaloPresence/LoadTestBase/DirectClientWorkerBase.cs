using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Orleans.Runtime;

namespace LoadTestBase
{
    public abstract class DirectClientWorkerBase : MarshalByRefObject
    {
        protected string Name;
        protected int _workerIndex;
        protected long nRequests;
        protected int _nThreads;
        protected int _numFinishedRequests;
        protected int _reportBlockSize;
        protected int _pipelineSize;
        protected Callback _callback;

        private int[] _txnsPerRequest;
        private int _successTxns = 0;
        private int _failTxns = 0;
        private int _busyTxns = 0;
        private int _lateTxns = 0;
        private IPipeline pipe;

        abstract protected Task IssueRequest(int requestNumber, int threadNumber);

        public virtual void Initialize(int workerIndex, long numRequests, int numThreads, int reportBlockSize, int pipelineSize, int targetLoadPerWorker, Callback callback)
        {
            Name = AppDomain.CurrentDomain.FriendlyName + " Worker " + workerIndex;
            _workerIndex = workerIndex;
            nRequests = numRequests;
            _nThreads = numThreads;
            _numFinishedRequests = 0;
            _reportBlockSize = reportBlockSize;
            _pipelineSize = pipelineSize;
            _callback = callback;

            _txnsPerRequest = new int[numThreads];

            if (targetLoadPerWorker > 0)
            {
                pipe = new PoissonPipeline(targetLoadPerWorker);
            }
            else
            {
                pipe = new AsyncPipeline(_pipelineSize);
            }
        }

        public virtual void Uninitialize()
        {
        }

        public virtual void Run()
        {
            int slowStartIteractions = 0;
            List<Task> threads = new List<Task>();

            long numRequestsPerThread = nRequests / _nThreads;
            for (int j = 0; j < _nThreads; j++)
            {
                int threadNum = j;
                Task thread = Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < numRequestsPerThread; i++)
                    {
                        long requestsPerThread = numRequestsPerThread;
                        int requestNumber = (int)(threadNum * requestsPerThread + i);
                        SetDefaultTxnsPerformed(threadNum);
                        try
                        {
                            Task response =
                                IssueRequest(requestNumber, threadNum)
                                .ContinueWith(
                                    task =>
                                    {
                                        if (task.IsFaulted)
                                        {
                                            HandleException(task.Exception, _txnsPerRequest[threadNum]);
                                        }

                                        // IsCompleted will return true when the task is in one of the three final states: RanToCompletion, Faulted, or Canceled.
                                        if (task.Status.Equals(TaskStatus.RanToCompletion))
                                        {
                                            lock (this)
                                            {
                                                _numFinishedRequests++;
                                                int performedTxns = _txnsPerRequest[threadNum];
                                                _successTxns += performedTxns;
                                                TryReportBlock(ref _successTxns, ref _failTxns, ref _lateTxns, ref _busyTxns, pipe.Count);
                                            }
                                        }
                                    });
                            pipe.Add(response);
                        }
                        catch (Exception exc)
                        {
                            HandleException(exc, _txnsPerRequest[threadNum]);
                        }
                        //responses.Add(response);
                        if (slowStartIteractions++ < 100)
                            Thread.Sleep(100); // submit first 100 requests at a lower rate to prevent a burst at start
                    } // for i
                }); // Task.StartNew
                threads.Add(thread);
            } // for j

            //Task.JoinAll(responses).Wait();
            Task.WhenAll(threads).Wait();
            WriteProgress("Threads have exited; waiting for pipeline to empty...");
            pipe.Wait();
            WriteProgress("Pipeline emptied.");
        }

        private void HandleException(Exception exc, int txnCount)
        {
            lock (this)
            {
                _numFinishedRequests++;
                Exception ex = exc.GetBaseException();
                if (ex is TimeoutException)
                {
                    if (_lateTxns <= 0)
                    {
                        WriteProgress("Timeout: {0}", ex.Message);
                    }
                    _lateTxns += txnCount;
                }
                else if (ex is GatewayTooBusyException)
                {
                    if (_busyTxns <= 0)
                    {
                        WriteProgress("Server Busy: {0}", ex.Message);
                    }
                    _busyTxns += txnCount;
                }
                else
                {
                    WriteProgress("!! Exception: Message: {0}, StackTrace:{1}",
                                                                    ex.Message, ex.StackTrace);
                    _failTxns += txnCount;
                    //failures.Add(ex);
                }
                TryReportBlock(ref _successTxns, ref _failTxns, ref _lateTxns, ref _busyTxns, pipe.Count);
            }
        }

        private void TryReportBlock(ref int nSucc, ref int nFail, ref int nLate, ref int nBusy, int pipeCount)
        {
            if (_numFinishedRequests > 0 && _numFinishedRequests % _reportBlockSize == 0)
            {
                int numSucc = nSucc;
                int numFail = nFail;
                int numLate = nLate;
                int numBusy = nBusy;
                _callback.ReportBlock(Name, numSucc, numFail, numLate, numBusy, pipeCount, new Latency());
                lock (this)
                {
                    // just subtract what ever we reported now and forget about it.
                    nSucc -= numSucc;
                    nFail -= numFail;
                    nLate -= numLate;
                    nBusy -= numBusy;
                }
            }
        }

        public virtual void WriteProgress(string format, params object[] args)
        {
            LoadTestDriverBase.WriteProgress(format, args);
        }

        protected void SetTxnsPerformedPerRequest(int threadNum, int quantity)
        {
            _txnsPerRequest[threadNum] = quantity;
        }

        private void SetDefaultTxnsPerformed(int threadNum)
        {
            SetTxnsPerformedPerRequest(threadNum, 1);
        }
    }
}