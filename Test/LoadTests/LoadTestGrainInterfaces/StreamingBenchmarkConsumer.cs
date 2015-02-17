using System;
using System.Threading.Tasks;
using Orleans;

using Orleans.Streams;

namespace LoadTestGrainInterfaces
{
   public interface IStreamingBenchmarkConsumer : IGrain
    {
        Task StartConsumer(Guid streamId, string providerName);
        Task StopConsumer();
        Task SetVerbosity(double verbosity, long period);
    }

    public class StreamingBenchmarkConsumer : IStreamingBenchmarkConsumer, IAsyncObserver<StreamingBenchmarkItem>
    {
        private readonly Action<string> _logFunc;
        private readonly Func<string, IStreamProvider> _getProviderFunc;
        private Guid _streamId;
        private string _providerName;
        private LoggingFilter _loggingFilter;
        private int _refCnt;

        private StreamSubscriptionHandle<StreamingBenchmarkItem> _subscription;

        public StreamingBenchmarkConsumer(Action<string> logFunc, Func<string, IStreamProvider> getProviderFunc)
        {
            if (null == logFunc)
                throw new ArgumentNullException("logFunc");
            if (null == getProviderFunc)
                throw new ArgumentNullException("getProviderFunc");
            _logFunc = logFunc;
            _getProviderFunc = getProviderFunc;
            _subscription = null;
            _loggingFilter = new LoggingFilter(0, 0, null);
            _refCnt = 0;
        }

        public async Task StartConsumer(Guid streamId, string providerName)
        {
            if (streamId == null)
                throw new ArgumentNullException("streamId");

            _refCnt++;
            if (_loggingFilter.ShouldLog())
            {
                _logFunc(string.Format("StreamingBenchmarkConsumer.StartConsumer: streamId={0}, providerName={1}, _refCnt={2}", streamId, providerName, _refCnt));
            }

            if (null == _subscription)
            {
                _streamId = streamId;
                _providerName = providerName;
                IStreamProvider provider = _getProviderFunc(_providerName);
                IAsyncStream<StreamingBenchmarkItem> stream = provider.GetStream<StreamingBenchmarkItem>(_streamId, StreamingBenchmarkProducer.StreamNamespace);
                _subscription = await stream.SubscribeAsync(this);    
            }
        }

        public async Task StopConsumer()
        {
            if (null == _subscription)
                throw new InvalidOperationException("already stopped");
            --_refCnt;
            if (_loggingFilter.ShouldLog())
            {
                _logFunc(string.Format("StreamingBenchmarkConsumer.StopConsumer: streamId={0} _refCnt={1}", _streamId, _refCnt));
            }
            if (0 == _refCnt)
            {
                IStreamProvider provider = _getProviderFunc(_providerName);
                IAsyncStream<StreamingBenchmarkItem> stream = provider.GetStream<StreamingBenchmarkItem>(_streamId, StreamingBenchmarkProducer.StreamNamespace);
                await stream.UnsubscribeAsync(_subscription);
                _subscription = null;
            }
        }

        public Task SetVerbosity(double verbosity, long period)
        {
            _loggingFilter = new LoggingFilter(verbosity, period, _logFunc);
            return TaskDone.Done;
        }

        public Task OnNextAsync(StreamingBenchmarkItem item, StreamSequenceToken token)
        {
            if (!Object.Equals(item.StreamGuid, _streamId))
            {
                string excStr = String.Format("StreamingBenchmarkConsumer.OnNextAsync: received an item from the wrong stream." + " Got item {0} from stream = {1}, expecting stream = {2}", item, item.StreamGuid, _streamId);
                _logFunc(excStr);
                throw new ArgumentException(excStr);
            }

            if (_loggingFilter.ShouldLog())
            {
                string str = String.Format("StreamingBenchmarkConsumer.OnNextAsync: streamId={0}, item={1}", _streamId, item.Data);
                _logFunc(str);
            }

            return TaskDone.Done;
        }

        public Task OnCompletedAsync()
        {
            throw new NotImplementedException();
        }

        public Task OnErrorAsync(Exception ex)
        {
            throw new NotImplementedException();
        }
    }

    public interface IStreamingBenchmarkConsumerGrain : IGrain, IStreamingBenchmarkConsumer
    {}
}