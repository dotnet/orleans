using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Concurrency;

using UnitTestGrains;

namespace UnitTests.Streaming
{
    public class Streaming_ConsumerClientObject : IAsyncObserver<StreamItem>, IStreaming_ConsumerGrain
    {
        private readonly ConsumerObserver _consumer;
        private string _providerToUse;

        private Streaming_ConsumerClientObject(Logger logger)
        {
            _consumer = ConsumerObserver.NewObserver(logger);
        }

        public static Streaming_ConsumerClientObject NewObserver(Logger logger)
        {
            return new Streaming_ConsumerClientObject(logger);
        }

        public Task OnNextAsync(StreamItem item, StreamSequenceToken token = null)
        {
            return _consumer.OnNextAsync(item, token);
        }

        public Task OnCompletedAsync()
        {
            return _consumer.OnCompletedAsync();
        }

        public Task OnErrorAsync(Exception ex)
        {
            return _consumer.OnErrorAsync(ex);
        }

        public Task BecomeConsumer(Guid streamId, string providerToUse)
        {
            _providerToUse = providerToUse;
            return _consumer.BecomeConsumer(streamId, GrainClient.GetStreamProvider(providerToUse), null);
        }
        
        public Task BecomeConsumer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _providerToUse = providerToUse;
            return _consumer.BecomeConsumer(streamId, GrainClient.GetStreamProvider(providerToUse), streamNamespace);
        }

        public Task StopBeingConsumer()
        {
            return _consumer.StopBeingConsumer(GrainClient.GetStreamProvider(_providerToUse));
        }

        public Task<int> GetConsumerCount()
        {
            return _consumer.ConsumerCount;
        }

        public Task<int> GetItemsConsumed()
        {
            return _consumer.ItemsConsumed;
        }

        public Task DeactivateConsumerOnIdle()
        {
            return TaskDone.Done;
        }
    }

    public class Streaming_ProducerClientObject : IStreaming_ProducerGrain
    {
        private readonly ProducerObserver _producer;

        private Streaming_ProducerClientObject(Logger logger)
        {
            _producer = ProducerObserver.NewObserver(logger);
        }

        public static Streaming_ProducerClientObject NewObserver(Logger logger)
        {
            if (null == logger)
                throw new ArgumentNullException("logger");
            return new Streaming_ProducerClientObject(logger);
        }

        public Task BecomeProducer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _producer.BecomeProducer(streamId, GrainClient.GetStreamProvider(providerToUse), streamNamespace);
            return TaskDone.Done;
        }

        public Task ProduceSequentialSeries(int count)
        {
             return _producer.ProduceSequentialSeries(count);
        }

        public Task ProduceParallelSeries(int count)
        {
             return _producer.ProduceParallelSeries(count);
        }

        public Task<int> GetItemsProduced()
        {
            return _producer.ItemsProduced;
        }

        public Task ProducePeriodicSeries(int count)
        {
            return _producer.ProducePeriodicSeries(timerCallback =>
                    {
                        return new AsyncTaskSafeTimer(timerCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
                    }, count);
        }

        public Task<Guid> GetStreamId()
        {
            return _producer.StreamId;
        }

        public Task<string> GetProviderName()
        {
            return Task.FromResult(_producer.ProviderName);
        }

        public Task AddNewConsumerGrain(Guid consumerGrainId)
        {
            return _producer.AddNewConsumerGrain(consumerGrainId);
        }

        public Task<int> GetExpectedItemsProduced()
        {
            return _producer.ExpectedItemsProduced;
        }

        public Task<int> GetProducerCount()
        {
            return _producer.ProducerCount;
        }

        public Task StopBeingProducer()
        {
            return _producer.StopBeingProducer();
        }

        public Task VerifyFinished()
        {
            return _producer.VerifyFinished();
        }

        public Task DeactivateProducerOnIdle()
        {
            return TaskDone.Done;
        }
    }

    internal class ConsumerProxy
    {
        private readonly IStreaming_ConsumerGrain[] _targets;
        private readonly TraceLogger _logger;

        private ConsumerProxy(IStreaming_ConsumerGrain[] targets, TraceLogger logger)
        {
            _targets = targets;
            _logger = logger;
        }

        private static async Task<ConsumerProxy> NewConsumerProxy(Guid streamId, string streamProvider, IStreaming_ConsumerGrain[] targets, TraceLogger logger)
        {
            if (targets == null)
                throw new ArgumentNullException("targets");
            if (targets.Length == 0)
                throw new ArgumentException("caller must specify at least one target");
            if (String.IsNullOrWhiteSpace(streamProvider))
                throw new ArgumentException("Stream provider name is either null or whitespace", "streamProvider");
            if (logger == null)
                throw new ArgumentNullException("logger");

            ConsumerProxy newObj = new ConsumerProxy(targets, logger);
            await newObj.BecomeConsumer(streamId, streamProvider);
            return newObj;
        }

        public static Task<ConsumerProxy> NewConsumerGrainsAsync(Guid streamId, string streamProvider, TraceLogger logger, int[] grainIds = null, int grainCount = 1)
        {
            grainCount = grainIds != null ? grainIds.Length : grainCount;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainCount", "The grain count must be at least one");
            logger.Info("ConsumerProxy.NewConsumerGrainsAsync: multiplexing {0} consumer grains for stream {1}.", grainCount, streamId);
            var grains = new IStreaming_ConsumerGrain[grainCount];
            var dedup = new Dictionary<int, IStreaming_ConsumerGrain>();
            for (var i = 0; i < grainCount; ++i)
            {
                if (grainIds != null)
                {
                    // we deduplicate the grain references to ensure that IEnumerable.Distinct() works as intended.
                    if (dedup.ContainsKey(grainIds[i]))
                        grains[i] = dedup[grainIds[i]];
                    else
                    {
                        var gref = Streaming_ConsumerGrainFactory.GetGrain(grainIds[i], "UnitTestGrains.Streaming_ConsumerGrain");
                        grains[i] = gref;
                        dedup[grainIds[i]] = gref;
                    }
                }
                else
                {
                    grains[i] = Streaming_ConsumerGrainFactory.GetGrain(Guid.NewGuid(), "UnitTestGrains.Streaming_ConsumerGrain");
                }
            }
            return NewConsumerProxy(streamId, streamProvider, grains, logger);
        }

        public static Task<ConsumerProxy> NewProducerConsumerGrainsAsync(Guid streamId, string streamProvider, TraceLogger logger, int[] grainIds, bool useReentrantGrain)
        {
            int grainCount = grainIds.Length;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainIds", "The grain count must be at least one");
            logger.Info("ConsumerProxy.NewProducerConsumerGrainsAsync: multiplexing {0} consumer grains for stream {1}.", grainCount, streamId);
            var grains = new IStreaming_ConsumerGrain[grainCount];
            var dedup = new Dictionary<int, IStreaming_ConsumerGrain>();
            for (var i = 0; i < grainCount; ++i)
            {
                    // we deduplicate the grain references to ensure that IEnumerable.Distinct() works as intended.
                    if (dedup.ContainsKey(grainIds[i]))
                        grains[i] = dedup[grainIds[i]];
                    else
                    {
                        if (useReentrantGrain)
                        {
                            grains[i] = Streaming_Reentrant_ProducerConsumerGrainFactory.GetGrain(grainIds[i]);
                        }
                        else
                        {
                            grains[i] = Streaming_ProducerConsumerGrainFactory.GetGrain(grainIds[i], "UnitTestGrains.Streaming_ProducerConsumerGrain");
                        }
                        dedup[grainIds[i]] = grains[i];
                    }
                    }
            return NewConsumerProxy(streamId, streamProvider, grains, logger);
        }

        public static Task<ConsumerProxy> NewConsumerClientObjectsAsync(Guid streamId, string streamProvider, TraceLogger logger, int consumerCount = 1)
        {
            if (consumerCount < 1)
                throw new ArgumentOutOfRangeException("consumerCount", "argument must be 1 or greater");
            logger.Info("ConsumerProxy.NewConsumerClientObjectsAsync: multiplexing {0} consumer client objects for stream {1}.", consumerCount, streamId);
            var objs = new IStreaming_ConsumerGrain[consumerCount];
            for (var i = 0; i < consumerCount; ++i)
                objs[i] = Streaming_ConsumerClientObject.NewObserver(logger);
            return NewConsumerProxy(streamId, streamProvider, objs, logger);
        }

        public static ConsumerProxy NewConsumerGrainAsync_WithoutBecomeConsumer(Guid consumerGrainId, TraceLogger logger, string grainClassName = "UnitTestGrains.Streaming_ConsumerGrain")
        {
            if (logger == null)
                throw new ArgumentNullException("logger");

            var grains = new IStreaming_ConsumerGrain[1];
            grains[0] = Streaming_ConsumerGrainFactory.GetGrain(consumerGrainId, grainClassName);
            ConsumerProxy newObj = new ConsumerProxy(grains, logger);
            return newObj;
        }

        private async Task BecomeConsumer(Guid streamId, string providerToUse)
        {
            List<Task> tasks = new List<Task>();
            foreach (var target in _targets)
            {
                Task t = target.BecomeConsumer(streamId, providerToUse, null);
                // Consider: remove this await, let the calls go in parallel. 
                // Have to do it for now to prevent multithreaded scheduler bug from happening.
                // await t;
                tasks.Add(t);
            }
            await Task.WhenAll(tasks);
        }

        private async Task<int> GetItemsConsumed()
        {
            var tasks = _targets.Distinct().Select(t => t.GetItemsConsumed()).ToArray();
            await Task.WhenAll(tasks);
            return tasks.Sum(t => t.Result);
        }

        public Task<int> ItemsConsumed
        {
            get { return GetItemsConsumed(); }
        }

        private async Task<int> GetConsumerCount()
        {
            var tasks = _targets.Distinct().Select(p => p.GetConsumerCount()).ToArray();
            await Task.WhenAll(tasks);
            return tasks.Sum(t => t.Result);
        }

        public Task<int> ConsumerCount
        {
            get { return GetConsumerCount(); }
        }        

        public Task StopBeingConsumer()
        {
            var tasks = _targets.Distinct().Select(c => c.StopBeingConsumer()).ToArray();
            return Task.WhenAll(tasks);
        }

        public async Task DeactivateOnIdle()
        {
            var tasks = _targets.Distinct().Select(t => t.DeactivateConsumerOnIdle()).ToArray();
            await Task.WhenAll(tasks);
        }

        public Task<int> GetNumActivations()
        {
            return ConsumerProxy.GetNumActivations(_targets.Distinct());
    }

        public static async Task<int> GetNumActivations(IEnumerable<IGrain> targets)
        {
            var grainIds = targets.Distinct().Where(t => t is GrainReference).Select(t => ((GrainReference)t).GrainId).ToArray();
            IManagementGrain systemManagement = ManagementGrainFactory.GetGrain(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            var tasks = grainIds.Select(g => systemManagement.GetGrainActivationCount(GrainReference.FromGrainId(g))).ToArray();
            await Task.WhenAll(tasks);
            return tasks.Sum(t => t.Result);
        }
    }

    internal class ProducerProxy
    {
        private readonly IStreaming_ProducerGrain[] _targets;
        private readonly TraceLogger _logger;
        private readonly Guid _streamId;
        private readonly string _providerName;
        private readonly InterlockedFlag _cleanedUpFlag;

        public Task<int> ExpectedItemsProduced
        {
            get { return GetExpectedItemsProduced(); }
        }

        public string ProviderName { get { return _providerName; } }
        public Guid StreamId { get { return _streamId; } }

        private ProducerProxy(IStreaming_ProducerGrain[] targets, Guid streamId, string providerName, TraceLogger logger)
        {
            _targets = targets;
            _logger = logger;
            _streamId = streamId;
            _providerName = providerName;
            _cleanedUpFlag = new InterlockedFlag();
        }

        private static async Task<ProducerProxy> NewProducerProxy(IStreaming_ProducerGrain[] targets, Guid streamId, string streamProvider, string streamNamespace, TraceLogger logger)
        {
            if (targets == null)
                throw new ArgumentNullException("targets");
            if (String.IsNullOrWhiteSpace(streamProvider))
                throw new ArgumentException("Stream provider name is either null or whitespace", "streamProvider");
            if (logger == null)
                throw new ArgumentNullException("logger");

            ProducerProxy newObj = new ProducerProxy(targets, streamId, streamProvider, logger);
            await newObj.BecomeProducer(streamId, streamProvider, streamNamespace);
            return newObj;
        }

        public static Task<ProducerProxy> NewProducerGrainsAsync(Guid streamId, string streamProvider, string streamNamespace, TraceLogger logger, int[] grainIds = null, int grainCount = 1)
        {
            grainCount = grainIds != null ? grainIds.Length : grainCount;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainCount", "The grain count must be at least one");
            logger.Info("ProducerProxy.NewProducerGrainsAsync: multiplexing {0} producer grains for stream {1}.", grainCount, streamId);
            var grains = new IStreaming_ProducerGrain[grainCount];
            var dedup = new Dictionary<int, IStreaming_ProducerGrain>();
            for (var i = 0; i < grainCount; ++i)
            {
                if (grainIds != null)
                {
                    // we deduplicate the grain references to ensure that IEnumerable.Distinct() works as intended.
                    if (dedup.ContainsKey(grainIds[i]))
                        grains[i] = dedup[grainIds[i]];
                    else
                    {
                        var gref = Streaming_ProducerGrainFactory.GetGrain(grainIds[i], "UnitTestGrains.Streaming_ProducerGrain");
                        grains[i] = gref;
                        dedup[grainIds[i]] = gref;
                    }
                }
                else
                {
                    grains[i] = Streaming_ProducerGrainFactory.GetGrain(Guid.NewGuid(), "UnitTestGrains.Streaming_ProducerGrain");
                }
            }
            return NewProducerProxy(grains, streamId, streamProvider, streamNamespace, logger);
        }

        public static Task<ProducerProxy> NewProducerConsumerGrainsAsync(Guid streamId, string streamProvider, TraceLogger logger, int[] grainIds, bool useReentrantGrain)
        {
            int grainCount = grainIds.Length;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainIds", "The grain count must be at least one");
            logger.Info("ConsumerProxy.NewProducerConsumerGrainsAsync: multiplexing {0} producer grains for stream {1}.", grainCount, streamId);
            var grains = new IStreaming_ProducerGrain[grainCount];
            var dedup = new Dictionary<int, IStreaming_ProducerGrain>();
            for (var i = 0; i < grainCount; ++i)
            {
                    // we deduplicate the grain references to ensure that IEnumerable.Distinct() works as intended.
                    if (dedup.ContainsKey(grainIds[i]))
                        grains[i] = dedup[grainIds[i]];
                    else
                    {
                        if (useReentrantGrain)
                        {
                            grains[i] = Streaming_Reentrant_ProducerConsumerGrainFactory.GetGrain(grainIds[i]);
                        }
                        else
                        {
                            grains[i] = Streaming_ProducerConsumerGrainFactory.GetGrain(grainIds[i], "UnitTestGrains.Streaming_ProducerConsumerGrain");
                        }
                        dedup[grainIds[i]] = grains[i];
                    }                    
                }
            return NewProducerProxy(grains, streamId, streamProvider, null, logger);
        }

        public static Task<ProducerProxy> NewProducerClientObjectsAsync(Guid streamId, string streamProvider,  string streamNamespace, TraceLogger logger, int producersCount = 1)
        {            
            if (producersCount < 1)
                throw new ArgumentOutOfRangeException("producersCount", "The producer count must be at least one");
            var producers = new IStreaming_ProducerGrain[producersCount];
            for (var i = 0; i < producersCount; ++i)
                producers[i] = Streaming_ProducerClientObject.NewObserver(logger);
            logger.Info("ProducerProxy.NewProducerClientObjectsAsync: multiplexing {0} producer client objects for stream {1}.", producersCount, streamId);
            return NewProducerProxy(producers, streamId, streamProvider, streamNamespace, logger);
        }

        private Task BecomeProducer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            return Task.WhenAll(_targets.Select(
                target => 
                    target.BecomeProducer(streamId, providerToUse, streamNamespace)).ToArray());
        }

        public async Task ProduceSequentialSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            foreach (var t in _targets.Distinct())
                await t.ProduceSequentialSeries(count); 
        }
            
        public Task ProduceParallelSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            return Task.WhenAll(_targets.Distinct().Select(t => t.ProduceParallelSeries(count)).ToArray());
        }

        public Task ProducePeriodicSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            return Task.WhenAll(_targets.Distinct().Select(t => t.ProducePeriodicSeries(count)).ToArray());
        }

        public async Task<Guid> AddNewConsumerGrain()
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            if (_targets.Length != 1)
                throw new InvalidOperationException("This method is only supported for singular producer cases");
            // disabled temporarily.
            // return _targets[0].AddNewConsumerGrain();
            Guid consumerGrainId = Guid.NewGuid();
            await _targets[0].AddNewConsumerGrain(consumerGrainId);
            return consumerGrainId;
        }

        private async Task<int> GetExpectedItemsProduced()
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            var tasks = _targets.Distinct().Select(t => t.GetExpectedItemsProduced()).ToArray();
            await Task.WhenAll(tasks);
            return tasks.Sum(t => t.Result);
        }

        private async Task<int> GetProducerCount()
        {
            var tasks = _targets.Distinct().Select(p => p.GetProducerCount()).ToArray();
            await Task.WhenAll(tasks);
            return tasks.Sum(t => t.Result);
        }

        public Task<int> ProducerCount
        {
            get
            {
                // This method is used by the test code to verify that the object has in fact been disposed properly,
                // so we choose not to throw if the object has already been disposed.
                return GetProducerCount();
            }
        }

        public async Task StopBeingProducer()
        {
            if (!_cleanedUpFlag.TrySet())
                return;
                
            var tasks = new List<Task>();
            foreach (var i in _targets.Distinct())
            {
                tasks.Add(i.StopBeingProducer());
            }
            await Task.WhenAll(tasks);

            tasks = new List<Task>();
            foreach (var i in _targets.Distinct())
            {
                tasks.Add(i.VerifyFinished());
            }
            await Task.WhenAll(tasks);
        }

        public Task DeactivateOnIdle()
        {
            var tasks = _targets.Distinct().Select(t => t.DeactivateProducerOnIdle()).ToArray();
            return Task.WhenAll(tasks);
        }

        public Task<int> GetNumActivations()
        {
            return ConsumerProxy.GetNumActivations(_targets.Distinct());
        }
    }
}