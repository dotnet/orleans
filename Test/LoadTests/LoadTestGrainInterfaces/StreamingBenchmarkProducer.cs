using System;
using System.Threading.Tasks;
using Orleans;


using Orleans.Streams;

namespace LoadTestGrainInterfaces
{
    public interface IStreamingBenchmarkProducer : IGrain
    {
        Task StartProducer(Guid streamId, string providerName);
        Task Push(StreamingBenchmarkItem item);
        Task StopProducer();
        Task SetVerbosity(double verbosity, long period);
    }

    public class StreamingBenchmarkProducer : IStreamingBenchmarkProducer, IAsyncObserver<StreamingBenchmarkItem>
    {
        private readonly Action<string> _logFunc;
        private readonly Func<string, IStreamProvider> _getProviderFunc;
        private Guid _streamId;
        private LoggingFilter _loggingFilter;
        internal static readonly string StreamNamespace = "StreamingBenchmarkLoasTests";

        private IAsyncBatchObserver<StreamingBenchmarkItem> _producer;

        public StreamingBenchmarkProducer(Action<string> logFunc, Func<string, IStreamProvider> getProviderFunc)
        {
            if (null == logFunc)
                throw new ArgumentNullException("logFunc");
            if (null == getProviderFunc)
                throw new ArgumentNullException("getProviderFunc");
            _logFunc = logFunc;
            _getProviderFunc = getProviderFunc;
            _loggingFilter = new LoggingFilter(0, 0, null);
            _producer = null;
        }

        public Task StartProducer(Guid streamId, string providerName)
        {
            if (streamId == null)
                throw new ArgumentNullException("streamId");

            if (null == _producer)
            {
                if (_loggingFilter.ShouldLog())
                {
                    _logFunc(string.Format("StreamingBenchmarkProducer.StartProducer: streamId={0}, providerName={1}", streamId, providerName));
                }
                _streamId = streamId;
                IStreamProvider provider = _getProviderFunc(providerName);
                IAsyncStream<StreamingBenchmarkItem> stream = provider.GetStream<StreamingBenchmarkItem>(streamId, StreamingBenchmarkProducer.StreamNamespace);
                _producer = stream;

                return TaskDone.Done;
            }
            else
                throw new InvalidOperationException("redundant call");
        }

        public Task StopProducer()
        {
            if (null == _producer)
                throw new InvalidOperationException("already stopped");
            if (_loggingFilter.ShouldLog())
                _logFunc(string.Format("StreamingBenchmarkProducer.StopProducer: streamId={0}", _streamId));
            _producer = null;
            return TaskDone.Done;
        }

        public Task Push(StreamingBenchmarkItem item)
        {
            return OnNextAsync(item);
        }
        public Task SetVerbosity(double verbosity, long period)
        {
            _loggingFilter = new LoggingFilter(verbosity, period, _logFunc);
            return TaskDone.Done;
        }

        public async Task OnNextAsync(StreamingBenchmarkItem item, StreamSequenceToken token = null)
        {
            if (!Object.Equals(item.StreamGuid, _streamId))
            {
                string excStr = String.Format("StreamingBenchmarkProducer.OnNextAsync: received an item from the wrong stream." + " Got item {0} from stream = {1}, expecting stream = {2}", item, item.StreamGuid, _streamId);
                _logFunc(excStr);
                throw new ArgumentException(excStr);
            }

            await _producer.OnNextAsync(item);

            if (_loggingFilter.ShouldLog())
            {
                string str = String.Format("StreamingBenchmarkProducer.OnNextAsync: streamId={0}, item={1}", _streamId, item.Data);
                _logFunc(str);
            }
        }

        public Task OnCompletedAsync()
        {
            return _producer.OnCompletedAsync();
        }

        public Task OnErrorAsync(Exception ex)
        {
            return _producer.OnErrorAsync(ex);
        }
    }

    public interface IStreamingBenchmarkProducerGrain : IGrain, IStreamingBenchmarkProducer
    {}
}