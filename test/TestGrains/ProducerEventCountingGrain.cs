using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class ProducerEventCountingGrain : BaseGrain, IProducerEventCountingGrain
    {
        private IAsyncObserver<int> _producer;
        private int _numProducedItems;
        private Logger _logger;

        public override Task OnActivateAsync()
        {
            _logger = GetLogger("ProducerEventCountingGrain " + IdentityString);
            _logger.Info("Producer.OnActivateAsync");
            _numProducedItems = 0;
            return base.OnActivateAsync();
        }

        public override async Task OnDeactivateAsync()
        {
            _logger = GetLogger("ProducerEventCountingGrain " + IdentityString);
            _logger.Info("Producer.OnDeactivateAsync");
            _numProducedItems = 0;
            await base.OnDeactivateAsync();
        }

        public Task BecomeProducer(Guid streamId, string providerToUse)
        {
            _logger.Info("Producer.BecomeProducer");
            if (streamId == null)
            {
                throw new ArgumentNullException("streamId");
            }
            if (String.IsNullOrEmpty(providerToUse))
            {
                throw new ArgumentNullException("providerToUse");
            }
            IStreamProvider streamProvider = GetStreamProvider(providerToUse);
            IAsyncStream<int> stream = streamProvider.GetStream<int>(streamId, ConsumerEventCountingGrain.StreamNamespace);
            _producer = stream;
            return TaskDone.Done;
        }

        public Task<int> GetNumberProduced()
        {
            return Task.FromResult(_numProducedItems);
        }

        public async Task SendEvent()
        {
            _logger.Info("Producer.SendEvent called");
            if (_producer == null)
            {
                throw new ApplicationException("Not yet a producer on a stream.  Must call BecomeProducer first.");
            }
            
            await _producer.OnNextAsync(_numProducedItems + 1);

            // update after send in case of error
            _numProducedItems++;
            _logger.Info("Producer.SendEvent - TotalSent: ({0})", _numProducedItems);
        }
    }
}