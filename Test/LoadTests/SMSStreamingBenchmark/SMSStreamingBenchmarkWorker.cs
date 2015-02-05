using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LoadTestBase;
using LoadTestGrainInterfaces;
using Orleans.Runtime;


namespace SMSStreamingBenchmark
{
    public class SMSStreamingBenchmarkWorker : OrleansClientWorkerBase
    {
        private TestStream[] _streams;
        public const double DefaultVerbosity = 0.0;
        private LoggingFilter _loggingFilter;
        private int _startPoint;
        private Guid[] _guidPool;
        private int _nextGuidInPool;

        // This is an example of worker initialization.
        // Pre-create grains, per-allocate data buffers, etc...
        public void ApplicationInitialize(int streamCount, int consumersPerStream, bool shareStreams, double verbosity = DefaultVerbosity)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            if (streamCount < 1)
                throw new ArgumentOutOfRangeException("streamCount");

            _loggingFilter = 
                new LoggingFilter(
                    verbosity, 
                    streamCount,
                    msg =>
                        WriteProgress(msg));
            WriteProgress(string.Format("Worker.ApplicationInitialize: verbosity={0} (max=1, none=0, all=1.0)", verbosity));

            streamCount = (streamCount > nRequests) ? (int)nRequests : streamCount;

            // we need 1 guid for each stream producer and 1 guid for each consumer that'll be associated with said stream.
            InitializeGuidPool(streamCount * (consumersPerStream + 1), shareStreams);

            AsyncPipeline initPipeline = new AsyncPipeline(500);
            // todo: parameterize
            const string providerName = "SMSProvider";
            Random rng = new Random();
            _startPoint = rng.Next(streamCount);

            // we need `streamCount` guids for our streams. we get them in bulk now so that we can use a different initialization order to reduce potential contention during initialization.
            Guid[] streamGuids = new Guid[streamCount];
            for (int i = 0; i < streamGuids.Length; ++i)
                streamGuids[i] = NextGuid();
            _streams = new TestStream[streamCount];
            for (int i = 0; i < _streams.Length; ++i)
            {
                int index = (i + _startPoint) % _streams.Length;
                // we use the chance to log (verbosity) to determine which streams should log. this prevents multiplicative factors in test scale from creating too much logging.
                LoggingFilter lf = _loggingFilter.ShouldLog() ? _loggingFilter.Clone() : null;
                _streams[index] = new TestStream(initPipeline, i, streamGuids[index], providerName, consumersPerStream, lf, NextGuid);
            }

            WriteProgress("Waiting on pipeline to empty...");
            initPipeline.Wait();
            stopwatch.Stop();
            WriteProgress(string.Format("Done ApplicationInitialize by worker {0} in {1} seconds", Name, stopwatch.Elapsed.TotalSeconds));
        }

        private void InitializeGuidPool(int count, bool shareStreams)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException("count");

            IGuidPoolGrain pool;
            if (shareStreams)
                pool = GuidPoolGrainFactory.GetGrain(0);
            else
                pool = GuidPoolGrainFactory.GetGrain(Guid.NewGuid());
            WriteProgress("Worker.GetGuids: fetching {0} guids; shared={1}", count, shareStreams);
            Guid[] guids = pool.GetGuids(count).Result;
            for (int i = 0; i < guids.Length; ++i)
            {
                if (_loggingFilter.ShouldLog())
                {
                    WriteProgress("Worker.ApplicationInitialize: guid #{0} is {1}; shared={2}", i, guids[i], shareStreams);
                }
            }
            WriteProgress("Worker.ApplicationInitialize: {0} guids fetched", count);
            _guidPool = guids;
            _nextGuidInPool = 0;
        }

        private Guid NextGuid()
        {
            return _guidPool[_nextGuidInPool++];
        }

        protected override Task IssueRequest(int requestNumber, int threadNumber)
        {
            try
            {
                int index = (requestNumber + _startPoint + threadNumber) % _streams.Length;
                return _streams[index].IssueRequestAsync(requestNumber, threadNumber);
            }
            catch (Exception e)
            {
                WriteProgress("SMSStreamingBenchmarkWorker.IssueRequest: FAIL {0}", e.ToString());
                WriteProgress("\n\n*********************************************************\n");
                throw;
            }
        }

        private class TestStream
        {
            private readonly Guid _streamId;
            private readonly IStreamingBenchmarkProducer _producer;
            private readonly List<IStreamingBenchmarkConsumer> _consumers;
            private readonly LoggingFilter _logFilter;
            private readonly int _myIndex;

            public TestStream(AsyncPipeline pipeline, int myIndex, Guid streamId, string providerName, int consumerCount, LoggingFilter logFilter, Func<Guid> nextGuidFunc)
            {
                if (null == pipeline)
                    throw new ArgumentNullException("pipeline");
                if (myIndex < 0)
                    throw new ArgumentOutOfRangeException("myIndex", myIndex, "cannot be negative");
                if (streamId == null)
                    throw new ArgumentNullException("streamId");
                if (string.IsNullOrWhiteSpace(providerName))
                    throw new ArgumentNullException("providerName");
                if (consumerCount < 0)
                    throw new ArgumentOutOfRangeException("consumerCount", consumerCount, "cannot be negative");

                _logFilter = logFilter ?? new LoggingFilter(0, 0, null);
                _myIndex = myIndex;
                _streamId = streamId;

                _consumers = new List<IStreamingBenchmarkConsumer>();

                for (int j = 0; j < consumerCount; ++j)
                {
                    IStreamingBenchmarkConsumerGrain g = StreamingBenchmarkConsumerGrainFactory.GetGrain(nextGuidFunc());
                    pipeline.Add(g.SetVerbosity(_logFilter.Verbosity, _logFilter.Period));
                    pipeline.Add(g.StartConsumer(_streamId, providerName));
                    _consumers.Add(g);
                }

                _producer = new StreamingBenchmarkProducer(
                    msg =>
                        LoadTestDriverBase.WriteProgress(string.Format("[P#{0}] {1}", _myIndex, msg)),
                    Orleans.GrainClient.GetStreamProvider);
                pipeline.Add(_producer.SetVerbosity(_logFilter.Verbosity, _logFilter.Period));
                pipeline.Add(_producer.StartProducer(_streamId, providerName));
            }

            public async Task IssueRequestAsync(int requestNumber, int threadNumber)
            {
                bool shouldLog = _logFilter.ShouldLog((ulong)requestNumber);
                if (shouldLog)
                {
                    LoadTestDriverBase.WriteProgress("TestStream.IssueRequestAsync: start. requestNumber={0}, threadNumber={1}, index={2}", requestNumber, threadNumber, _myIndex);
                }

                StreamingBenchmarkItem item = new StreamingBenchmarkItem(requestNumber, _streamId);
                await _producer.Push(item);

                if (shouldLog)
                    LoadTestDriverBase.WriteProgress("TestStream.IssueRequestAsync: done. requestNumber={0}", requestNumber);
            }

        }
    }
}