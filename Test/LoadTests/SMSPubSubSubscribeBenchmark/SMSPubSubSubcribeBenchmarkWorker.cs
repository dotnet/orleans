using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LoadTestBase;
using LoadTestGrainInterfaces;

using Orleans.Runtime;
using Orleans.Streams;
using LoadTest.Streaming.GrainInterfaces;

namespace LoadTest.Streaming
{
    public class SMSPubSubSubcribeBenchmarkWorker : OrleansClientWorkerBase
    {
        const string providerName = "SMSProvider";
        private TestStream[] _streams;
        public const double DefaultVerbosity = 0.0;
        private LoggingFilter _loggingFilter;
        private int _startPoint;
        private int _consumersPerStream;
        private bool _doUnsubscribe;
        protected Guid[] _grainIds;
        private static readonly Random rng = new Random();

        // This is an example of worker initialization.
        // Pre-create grains, per-allocate data buffers, etc...
        public void ApplicationInitialize(
            int streamCount, 
            int consumersPerStream, 
            int producersPerStream, 
            bool doUnsubscribe, 
            int startBarrierSize, 
            bool shareStreams, 
            double verbosity = DefaultVerbosity)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            if (streamCount < 1)
                throw new ArgumentOutOfRangeException("streamCount");

            _loggingFilter = new LoggingFilter(verbosity, streamCount, msg => WriteProgress(msg));
            WriteProgress("Worker.ApplicationInitialize: verbosity={0} (max=1, none=0, all=1.0)", verbosity);

            AsyncPipeline initPipeline = new AsyncPipeline(_pipelineSize);
            _startPoint = rng.Next(streamCount);
            _consumersPerStream = consumersPerStream;
            _doUnsubscribe = doUnsubscribe;

            streamCount = (streamCount > nRequests) ? (int) nRequests : streamCount;

            // 1 guid for each producer and 1 for each consumer grain that will be used.
            int numGrains = streamCount * (consumersPerStream + producersPerStream);
            // We need `lots of guids for our grains. we get them in bulk now so that we can use a different initialization order to reduce potential contention during initialization.
            _grainIds = new Guid[numGrains];
            for (int i = 0; i < _grainIds.Length; ++i)
            {
                _grainIds[i] = Guid.NewGuid();
            }

            _streams = new TestStream[streamCount];
            for (int i = 0; i < _streams.Length; ++i)
            {
                int index = (i + _startPoint) % _streams.Length;
                // we use the chance to log (verbosity) to determine which streams should log. this prevents multiplicative factors in test scale from creating too much logging.
                LoggingFilter lf = _loggingFilter.ShouldLog() ? _loggingFilter.Clone() : null;
                _streams[index] = new TestStream(
                    initPipeline, 
                    index, 
                    _grainIds, 
                    providerName, 
                    consumersPerStream, 
                    producersPerStream,
                    shareStreams,
                    lf);
            }

            WriteProgress("Waiting on pipeline to empty...");
            initPipeline.Wait();
            stopwatch.Stop();
            WriteProgress("Done ApplicationInitialize by worker {0} in {1} seconds", 
                Name, stopwatch.Elapsed.TotalSeconds);

            // the following number should match the number of clients launched.
            WaitAtStartBarrier(startBarrierSize).Wait();
        }

        protected override async Task IssueRequest(int requestNumber, int threadNumber)
        {
            try
            {
                int index = (requestNumber + _startPoint + threadNumber) % _streams.Length;

                // This test counts number of [un]subscriptions per second, not number of calls to IssueRequest(). 
                // We count one subscription request per consumer per call to IssueRequest(). 
                // If the test case is also doing Unsubscribes, then we also count one unsubscription request per consumer as well.
                // One stream is handled per request and there are _consumersPerStream consumers per stream. 
                // We don't count producers because initializing a producer doesn't result in a subscription.
                int txnsPerIteration = _consumersPerStream * (_doUnsubscribe ? 2 : 1);
                SetTxnsPerformedPerRequest(threadNumber, txnsPerIteration);

                await _streams[index].IssueRequestAsync(requestNumber, threadNumber, _doUnsubscribe);
            }
            catch (Exception e)
            {
                WriteProgress("Worker.IssueRequest: FAIL {0}", e.ToString());
                WriteProgress("\n\n*********************************************************\n");
                throw;
            }
        }

        private class TestStream
        {
            private readonly string _streamProviderName;
            private readonly List<IStreamProducerGrain> _producers = new List<IStreamProducerGrain>();
            private readonly List<IStreamConsumerGrain> _consumers = new List<IStreamConsumerGrain>();
            private readonly LoggingFilter _logFilter;
            private readonly int _myIndex;
            private readonly bool _shareStreams;
            private Guid _streamId;
            
            public TestStream(
                AsyncPipeline initPipeline,
                int myIndex,
                Guid[] grainIds,
                string providerName,
                int consumerCount,
                int producerCount,
                bool shareStreams,
                LoggingFilter logFilter)
            {
                if (null == initPipeline)
                    throw new ArgumentNullException("initPipeline");
                if (myIndex < 0)
                    throw new ArgumentOutOfRangeException("myIndex", myIndex, "cannot be negative");
                if (string.IsNullOrWhiteSpace(providerName))
                    throw new ArgumentNullException("providerName");
                if (consumerCount < 0)
                    throw new ArgumentOutOfRangeException("consumerCount", consumerCount, "cannot be negative");
                if (producerCount < 0)
                    throw new ArgumentOutOfRangeException("producerCount", producerCount, "cannot be negative");

                _logFilter = logFilter ?? new LoggingFilter(0, 0, null);
                _myIndex = myIndex;
                _streamProviderName = providerName;
                _shareStreams = shareStreams;
                _streamId = Guid.NewGuid();
                
                Initialize(initPipeline, grainIds, myIndex, consumerCount, producerCount);
            }

            private void Initialize(
                AsyncPipeline initPipeline,
                Guid[] grainIds, 
                int myStartIndex, 
                int consumerCount, 
                int producerCount)
            {
                // Pre-create consumer grains
                for (int j = 0; j < consumerCount; ++j)
                {
                    int idx = myStartIndex + j;
                    if (idx >= grainIds.Length) idx = 0;
                    Guid grainId = grainIds[idx];
                    IStreamConsumerGrain consumer = StreamConsumerGrainFactory.GetGrain(grainId);
                    _consumers.Add(consumer);

                    Task initConsumerTask = consumer.SetVerbosity(_logFilter.Verbosity, _logFilter.Period);
                    initPipeline.Add(initConsumerTask);
                    // Initialize grain, but don't actually call BecomeConsumer yet
                }

                // Pre-create producer grains
                for (int j = 0; j < producerCount; ++j)
                {
                    int idx = myStartIndex + consumerCount + j;
                    if (idx >= grainIds.Length) idx = 0;
                    Guid grainId = grainIds[idx];
                    IStreamProducerGrain producer = StreamProducerGrainFactory.GetGrain(grainId);
                    _producers.Add(producer);

                    Task initProducerTask = producer.SetVerbosity(_logFilter.Verbosity, _logFilter.Period);
                    initPipeline.Add(initProducerTask);
                    // Initialize grain, but don't actually call BecomeProducer yet
                }
                initPipeline.Wait();
            }

            public async Task IssueRequestAsync(int requestNumber, int threadNumber, bool doUnsubscribe)
            {
                if(!_shareStreams)
                    _streamId = Guid.NewGuid(); // New StreamId for each request if we are not reusing same streams

                var subscriptions = new List<StreamSubscriptionHandle<int>>() ;

                bool shouldLog = _logFilter.ShouldLog((ulong) requestNumber);
                if (shouldLog)
                {
                    LoadTestDriverBase.WriteProgress(
                        "TestStream.IssueRequestAsync: start. requestNumber={0}, threadNumber={1}, index={2}",
                        requestNumber, threadNumber, _myIndex);
                }

                // Do Subscribe
                if (shouldLog)
                {
                    LoadTestDriverBase.WriteProgress("Subscribe: {0} consumers on stream {1}", _consumers.Count, _streamId);
                }

                var publisherPromises = new List<Task>();
                foreach (var g in _producers)
                {
                    Task promise = g.BecomeProducer(_streamId, _streamProviderName);
                    publisherPromises.Add(promise);
                }
                // Ensure that all Publishers are initialized before we start subscribing any Consumers
                await Task.WhenAll(publisherPromises);

                var subscribePromises = new List<Task<StreamSubscriptionHandle<int>>>();
                foreach (var g in _consumers)
                {
                    Task<StreamSubscriptionHandle<int>> promise = g.BecomeConsumer(_streamId, _streamProviderName);

                    //if (subscribePromises.Count == 0
                    //    && _producers.Count == 0)
                    //{
                    //    // Wait for first consumer to complete synchronously, to avoid race for creation of PubSub grains for the new stream.
                    //    promise.Wait();
                    //}
                    subscribePromises.Add(promise);
                }
                await Task.WhenAll(subscribePromises);
                subscribePromises.ForEach(t => subscriptions.Add(t.Result));

                if (shouldLog)
                {
                    LoadTestDriverBase.WriteProgress(
                        "TestStream.IssueRequestAsync: Done subscribes. requestNumber={0}, threadNumber={1}, index={2} Subscriptions={3}",
                        requestNumber, threadNumber, _myIndex, subscriptions.Count);
                }

                if (doUnsubscribe)
                {
                    // Now do Unsubscribe as well
                    if (shouldLog)
                    {
                        LoadTestDriverBase.WriteProgress("Unsubscribe: {0} consumers on stream {1}", subscriptions.Count, _streamId);
                    }
                    var unsubscribePromises = new List<Task>();
                    int idx = 0;
                    foreach (var g in _consumers)
                    {
                        StreamSubscriptionHandle<int> subsHandle = subscriptions[idx++];
                        Task promise = g.RemoveConsumer(_streamId, _streamProviderName, subsHandle);
                        unsubscribePromises.Add(promise);
                    }

                    foreach (var g in _producers)
                    {
                        Task promise = g.ClearGrain();
                        unsubscribePromises.Add(promise);
                    }

                    await Task.WhenAll(unsubscribePromises);
                }

                if (shouldLog)
                {
                    LoadTestDriverBase.WriteProgress("TestStream.IssueRequestAsync: done. requestNumber={0}, threadNumber={1}, index={2}",
                        requestNumber, threadNumber, _myIndex);
                }
            }

        }
    }
}