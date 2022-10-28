using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.TestHelper;

namespace UnitTests.StreamingTests
{
    public class Streaming_ConsumerClientObject : IAsyncObserver<StreamItem>, IStreaming_ConsumerGrain
    {
        private readonly IClusterClient client;
        private readonly ConsumerObserver _consumer;
        private string _providerToUse;

        private Streaming_ConsumerClientObject(ILogger logger, IClusterClient client)
        {
            this.client = client;
            _consumer = ConsumerObserver.NewObserver(logger);
        }

        public static Streaming_ConsumerClientObject NewObserver(ILogger logger, IClusterClient client)
        {
            return new Streaming_ConsumerClientObject(logger, client);
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
            return _consumer.BecomeConsumer(streamId, this.client.GetStreamProvider(providerToUse), null);
        }
        
        public Task BecomeConsumer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _providerToUse = providerToUse;
            return _consumer.BecomeConsumer(streamId, this.client.GetStreamProvider(providerToUse), streamNamespace);
        }

        public Task StopBeingConsumer()
        {
            return _consumer.StopBeingConsumer(this.client.GetStreamProvider(_providerToUse));
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
            return Task.CompletedTask;
        }
    }

    public class Streaming_ProducerClientObject : IStreaming_ProducerGrain
    {
        private readonly ProducerObserver producer;
        private readonly IClusterClient client;
        private Streaming_ProducerClientObject(ILogger logger, IClusterClient client)
        {
            this.client = client;
            this.producer = ProducerObserver.NewObserver(logger, client);
        }

        public static Streaming_ProducerClientObject NewObserver(ILogger logger, IClusterClient client)
        {
            if (null == logger)
                throw new ArgumentNullException("logger");
            return new Streaming_ProducerClientObject(logger, client);
        }

        public Task BecomeProducer(Guid streamId, string providerToUse, string streamNamespace)
        {
            this.producer.BecomeProducer(streamId, this.client.GetStreamProvider(providerToUse), streamNamespace);
            return Task.CompletedTask;
        }

        public Task ProduceSequentialSeries(int count)
        {
             return this.producer.ProduceSequentialSeries(count);
        }

        public Task ProduceParallelSeries(int count)
        {
             return this.producer.ProduceParallelSeries(count);
        }

        public Task<int> GetItemsProduced()
        {
            return this.producer.ItemsProduced;
        }

        public Task ProducePeriodicSeries(int count)
        {
            return this.producer.ProducePeriodicSeries(timerCallback =>
                    {
                        return new AsyncTaskSafeTimer(NullLogger.Instance, timerCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
                    }, count);
        }

        public Task<Guid> GetStreamId()
        {
            return this.producer.StreamId;
        }

        public Task<string> GetProviderName()
        {
            return Task.FromResult(this.producer.ProviderName);
        }

        public Task AddNewConsumerGrain(Guid consumerGrainId)
        {
            return this.producer.AddNewConsumerGrain(consumerGrainId);
        }

        public Task<int> GetExpectedItemsProduced()
        {
            return this.producer.ExpectedItemsProduced;
        }

        public Task<int> GetProducerCount()
        {
            return this.producer.ProducerCount;
        }

        public Task StopBeingProducer()
        {
            return this.producer.StopBeingProducer();
        }

        public Task VerifyFinished()
        {
            return this.producer.VerifyFinished();
        }

        public Task DeactivateProducerOnIdle()
        {
            return Task.CompletedTask;
        }
    }

    internal class ConsumerProxy
    {
        private readonly IStreaming_ConsumerGrain[] _targets;
        private readonly ILogger _logger;
        private readonly IInternalGrainFactory grainFactory;

        private ConsumerProxy(IStreaming_ConsumerGrain[] targets, ILogger logger, IInternalGrainFactory grainFactory)
        {
            _targets = targets;
            _logger = logger;
            this.grainFactory = grainFactory;
        }

        private static async Task<ConsumerProxy> NewConsumerProxy(Guid streamId, string streamProvider, IStreaming_ConsumerGrain[] targets, ILogger logger, IInternalGrainFactory grainFactory)
        {
            if (targets == null)
                throw new ArgumentNullException("targets");
            if (targets.Length == 0)
                throw new ArgumentException("caller must specify at least one target");
            if (String.IsNullOrWhiteSpace(streamProvider))
                throw new ArgumentException("Stream provider name is either null or whitespace", "streamProvider");
            if (logger == null)
                throw new ArgumentNullException("logger");

            ConsumerProxy newObj = new ConsumerProxy(targets, logger, grainFactory);
            await newObj.BecomeConsumer(streamId, streamProvider);
            return newObj;
        }

        public static Task<ConsumerProxy> NewConsumerGrainsAsync(Guid streamId, string streamProvider, ILogger logger, IInternalGrainFactory grainFactory, Guid[] grainIds = null, int grainCount = 1)
        {
            grainCount = grainIds != null ? grainIds.Length : grainCount;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainCount", "The grain count must be at least one");
            logger.LogInformation("ConsumerProxy.NewConsumerGrainsAsync: multiplexing {GrainCount} consumer grains for stream {StreamId}.", grainCount, streamId);
            var grains = new IStreaming_ConsumerGrain[grainCount];
            var dedup = new Dictionary<Guid, IStreaming_ConsumerGrain>();
            var grainFullName = typeof(Streaming_ConsumerGrain).FullName;
            for (var i = 0; i < grainCount; ++i)
            {
                if (grainIds != null)
                {
                    // we deduplicate the grain references to ensure that IEnumerable.Distinct() works as intended.
                    if (dedup.ContainsKey(grainIds[i]))
                        grains[i] = dedup[grainIds[i]];
                    else
                    {
                        var gref = grainFactory.GetGrain<IStreaming_ConsumerGrain>(grainIds[i], grainFullName);
                        grains[i] = gref;
                        dedup[grainIds[i]] = gref;
                    }
                }
                else
                {
                    grains[i] = grainFactory.GetGrain<IStreaming_ConsumerGrain>(Guid.NewGuid(), grainFullName);
                }
            }
            return NewConsumerProxy(streamId, streamProvider, grains, logger, grainFactory);
        }

        public static Task<ConsumerProxy> NewProducerConsumerGrainsAsync(Guid streamId, string streamProvider, ILogger logger, int[] grainIds, bool useReentrantGrain, IInternalGrainFactory grainFactory)
        {
            int grainCount = grainIds.Length;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainIds", "The grain count must be at least one");
            logger.LogInformation("ConsumerProxy.NewProducerConsumerGrainsAsync: multiplexing {GrainCount} consumer grains for stream {StreamId}.", grainCount, streamId);
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
                            grains[i] = grainFactory.GetGrain<IStreaming_Reentrant_ProducerConsumerGrain>(grainIds[i]);
                        }
                        else
                        {
                            var grainFullName = typeof(Streaming_ProducerConsumerGrain).FullName;
                            grains[i] = grainFactory.GetGrain<IStreaming_ProducerConsumerGrain>(grainIds[i], grainFullName);
                        }
                        dedup[grainIds[i]] = grains[i];
                    }
                    }
            return NewConsumerProxy(streamId, streamProvider, grains, logger, grainFactory);
        }

        public static Task<ConsumerProxy> NewConsumerClientObjectsAsync(Guid streamId, string streamProvider, ILogger logger, IInternalClusterClient client, int consumerCount = 1)
        {
            if (consumerCount < 1)
                throw new ArgumentOutOfRangeException("consumerCount", "argument must be 1 or greater");
            logger.LogInformation("ConsumerProxy.NewConsumerClientObjectsAsync: multiplexing {ConsumerCount} consumer client objects for stream {StreamId}.", consumerCount, streamId);
            var objs = new IStreaming_ConsumerGrain[consumerCount];
            for (var i = 0; i < consumerCount; ++i)
                objs[i] = Streaming_ConsumerClientObject.NewObserver(logger, client);
            return NewConsumerProxy(streamId, streamProvider, objs, logger, client);
        }

        public static ConsumerProxy NewConsumerGrainAsync_WithoutBecomeConsumer(Guid consumerGrainId, ILogger logger, IInternalGrainFactory grainFactory, string grainClassName = "")
        {
            if (logger == null)
                throw new ArgumentNullException("logger");

            if (string.IsNullOrEmpty(grainClassName)) 
            {
                grainClassName = typeof(Streaming_ConsumerGrain).FullName;
            }

            var grains = new IStreaming_ConsumerGrain[1];
            grains[0] = grainFactory.GetGrain<IStreaming_ConsumerGrain>(consumerGrainId, grainClassName);
            ConsumerProxy newObj = new ConsumerProxy(grains, logger, grainFactory);
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

        public Task<int> GetNumActivations(IInternalGrainFactory grainFactory)
        {
            return GetNumActivations(_targets.Distinct(), grainFactory);
    }

        public static async Task<int> GetNumActivations(IEnumerable<IGrain> targets, IInternalGrainFactory grainFactory)
        {
            var grainIds = targets.Distinct().Where(t => t is GrainReference).Select(t => ((GrainReference)t).GrainId).ToArray();
            IManagementGrain systemManagement = grainFactory.GetGrain<IManagementGrain>(0);
            var tasks = grainIds.Select(g => systemManagement.GetGrainActivationCount((GrainReference)grainFactory.GetGrain(g))).ToArray();
            await Task.WhenAll(tasks);
            return tasks.Sum(t => t.Result);
        }
    }

    internal class ProducerProxy
    {
        private readonly IStreaming_ProducerGrain[] _targets;
        private readonly ILogger _logger;
        private readonly Guid _streamId;
        private readonly string _providerName;
        private readonly InterlockedFlag _cleanedUpFlag;

        public Task<int> ExpectedItemsProduced
        {
            get { return GetExpectedItemsProduced(); }
        }

        public string ProviderName { get { return _providerName; } }

        public Guid StreamIdGuid { get { return _streamId; } }

        public StreamId StreamId { get; }

        private ProducerProxy(IStreaming_ProducerGrain[] targets, Guid streamId, string providerName, ILogger logger)
        {
            _targets = targets;
            _logger = logger;
            _streamId = streamId;
            _providerName = providerName;
            _cleanedUpFlag = new InterlockedFlag();
            StreamId = StreamId.Create(null, streamId);
        }

        private static async Task<ProducerProxy> NewProducerProxy(IStreaming_ProducerGrain[] targets, Guid streamId, string streamProvider, string streamNamespace, ILogger logger)
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

        public static Task<ProducerProxy> NewProducerGrainsAsync(Guid streamId, string streamProvider, string streamNamespace, ILogger logger, IInternalGrainFactory grainFactory, Guid[] grainIds = null, int grainCount = 1)
        {
            grainCount = grainIds != null ? grainIds.Length : grainCount;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainCount", "The grain count must be at least one");
            logger.LogInformation("ProducerProxy.NewProducerGrainsAsync: multiplexing {GrainCount} producer grains for stream {StreamId}.", grainCount, streamId);
            var grains = new IStreaming_ProducerGrain[grainCount];
            var dedup = new Dictionary<Guid, IStreaming_ProducerGrain>();
            var producerGrainFullName = typeof(Streaming_ProducerGrain).FullName;
            for (var i = 0; i < grainCount; ++i)
            {
                if (grainIds != null)
                {
                    // we deduplicate the grain references to ensure that IEnumerable.Distinct() works as intended.
                    if (dedup.ContainsKey(grainIds[i]))
                        grains[i] = dedup[grainIds[i]];
                    else
                    {
                        var gref = grainFactory.GetGrain<IStreaming_ProducerGrain>(grainIds[i], producerGrainFullName);
                        grains[i] = gref;
                        dedup[grainIds[i]] = gref;
                    }
                }
                else
                {
                    grains[i] = grainFactory.GetGrain<IStreaming_ProducerGrain>(Guid.NewGuid(), producerGrainFullName);
                }
            }
            return NewProducerProxy(grains, streamId, streamProvider, streamNamespace, logger);
        }

        public static Task<ProducerProxy> NewProducerConsumerGrainsAsync(Guid streamId, string streamProvider, ILogger logger, int[] grainIds, bool useReentrantGrain, IInternalGrainFactory grainFactory)
        {
            int grainCount = grainIds.Length;
            if (grainCount < 1)
                throw new ArgumentOutOfRangeException("grainIds", "The grain count must be at least one");
            logger.LogInformation("ConsumerProxy.NewProducerConsumerGrainsAsync: multiplexing {GrainCount} producer grains for stream {StreamId}.", grainCount, streamId);
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
                            grains[i] = grainFactory.GetGrain<IStreaming_Reentrant_ProducerConsumerGrain>(grainIds[i]);
                        }
                        else
                        {
                            var grainFullName = typeof(Streaming_ProducerConsumerGrain).FullName;
                            grains[i] = grainFactory.GetGrain<IStreaming_ProducerConsumerGrain>(grainIds[i], grainFullName);
                        }
                        dedup[grainIds[i]] = grains[i];
                    }                    
                }
            return NewProducerProxy(grains, streamId, streamProvider, null, logger);
        }

        public static Task<ProducerProxy> NewProducerClientObjectsAsync(Guid streamId, string streamProvider,  string streamNamespace, ILogger logger, IClusterClient client, int producersCount = 1)
        {            
            if (producersCount < 1)
                throw new ArgumentOutOfRangeException("producersCount", "The producer count must be at least one");
            var producers = new IStreaming_ProducerGrain[producersCount];
            for (var i = 0; i < producersCount; ++i)
                producers[i] = Streaming_ProducerClientObject.NewObserver(logger, client);
            logger.LogInformation("ProducerProxy.NewProducerClientObjectsAsync: multiplexing {ProducerCount} producer client objects for stream {StreamId}.", producersCount, streamId);
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

        public Task<int> GetNumActivations(IInternalGrainFactory grainFactory)
        {
            return ConsumerProxy.GetNumActivations(_targets.Distinct(), grainFactory);
        }
    }
}