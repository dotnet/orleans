//#define USE_STORAGE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using LoadTest.Streaming.GrainInterfaces;
using LoadTestGrainInterfaces;

namespace LoadTest.Streaming.Grains
{
    public interface IStreamPubSubTestGrainState : IGrainState
    {
        // For producer and consumer 
        // -- only need to store this because of how we run our unit tests against multiple providers
        string StreamProviderName { get; set; }

        // For producer only.
        IAsyncStream<int> Stream { get; set; }
        bool IsProducer { get; set; }
        int NumMessagesSent { get; set; }
        int NumErrors { get; set; }

        // For consumer only.
        HashSet<StreamSubscriptionHandle<int>> ConsumerSubscriptionHandles { get; set; }
    }

    public abstract class StreamPubSubTestGrainBase : Grain<IStreamPubSubTestGrainState>
    {
        private const string StreamNamespace = "LoadTest.Streaming";

        protected Logger logger;
        protected LoggingFilter loggingFilter;
        protected string _lastProviderName;
        protected IStreamProvider _streamProvider;

        public Task SetVerbosity(double verbosity, long period)
        {
            loggingFilter = new LoggingFilter(verbosity, period, null);
            return TaskDone.Done;
        }

        protected void InitStream(Guid streamId, string providerName)
        {
            if (streamId == null) throw new ArgumentNullException("streamId", "Can't have null stream id");
            if (providerName == null) throw new ArgumentNullException("providerName", "Can't have null stream provider name");

            if (State.Stream != null && State.Stream.Guid != streamId)
            {
                if (logger.IsVerbose)
                    logger.Verbose("Stream already exists for StreamId={0} StreamProvider={1} - Resetting", State.Stream, providerName);

                // Note: in this test, we are deliberately not doing Unsubscribe consumers, just discard old stream and let auto-cleanup functions do their thing.
                InternalRemoveStreamConnections();
                State.ConsumerSubscriptionHandles.Clear();
                State.IsProducer = false;
                State.NumMessagesSent = 0;
                State.NumErrors = 0;
                State.Stream = null;
            }

            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("InitStream StreamId={0} StreamProvider={1}", streamId, providerName);
            }
            if (providerName != _lastProviderName)
            {
                _streamProvider = GetStreamProvider(providerName);
                _lastProviderName = providerName;
            }
            IAsyncStream<int> stream = _streamProvider.GetStream<int>(streamId, StreamNamespace);
            State.Stream = stream;
            State.StreamProviderName = providerName;

            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("InitStream returning with Stream={0} with ref type = {1}", State.Stream,
                    State.Stream.GetType().FullName);
            }
        }

        private void InternalRemoveStreamConnections()
        {
            // TODO: Revisit. This is a quick hack to unblock testing for resource leakages for streams.

            List<StreamSubscriptionHandle<int>> subsHandles = State.ConsumerSubscriptionHandles.ToList();
            var s = State.Stream as StreamImpl<int>;
            var c = s.GetConsumerInterface() as StreamConsumer<int>;
            foreach (var handle in subsHandles)
            {
                c.InternalRemoveObserver(handle);
            }

            var context = RuntimeContext.Current.ActivationContext as SchedulingContext;
            ActivationData currentActivation = context.Activation;
            StreamDirectory streamDirectory = currentActivation.GetStreamDirectory();
            streamDirectory.Clear();
        }

        internal static string EnumerableToString<T>(IEnumerable<T> collection, Func<T, string> toString = null,
                                                        string separator = ", ", bool putInBrackets = true)
        {
            if (collection == null)
            {
                if (putInBrackets) return "[]";
                else return "null";
            }
            var sb = new StringBuilder();
            if (putInBrackets) sb.Append("[");
            var enumerator = collection.GetEnumerator();
            bool firstDone = false;
            while (enumerator.MoveNext())
            {
                T value = enumerator.Current;
                string val;
                if (toString != null)
                    val = toString(value);
                else
                    val = value == null ? "null" : value.ToString();

                if (firstDone)
                {
                    sb.Append(separator);
                    sb.Append(val);
                }
                else
                {
                    sb.Append(val);
                    firstDone = true;
                }
            }
            if (putInBrackets) sb.Append("]");
            return sb.ToString();
        }
    }

#if USE_STORAGE
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
#endif
    public class StreamConsumerGrain : StreamPubSubTestGrainBase, IStreamConsumerGrain
    {
        protected MyStreamObserver<int> _observer;

        public override async Task OnActivateAsync()
        {
            logger = GetLogger(GetType().Name + "-" + IdentityString);
            if (logger.IsVerbose) logger.Info("OnActivateAsync");

            if (State.Stream != null && State.StreamProviderName != null)
            {
                if (State.ConsumerSubscriptionHandles.Count > 0)
                {
                    var handles = State.ConsumerSubscriptionHandles.ToArray();
                    if (logger.IsVerbose)
                    {
                        logger.Info("ReconnectConsumerHandles SubscriptionHandles={0} Grain={1}", LoadTestGrainInterfaces.Utils.EnumerableToString(handles), this.AsReference());
                    }
                    State.ConsumerSubscriptionHandles.Clear();
                    if (_observer == null)
                        _observer = new MyStreamObserver<int>(logger);
                    foreach (var handle in handles)
                    {
                        IAsyncStream<int> stream = handle.Stream;
                        StreamSubscriptionHandle<int> subsHandle = await stream.SubscribeAsync(_observer);
                        State.ConsumerSubscriptionHandles.Add(subsHandle);
                    }
                }
            }
            else
            {
                if (logger.IsVerbose) logger.Info("Not conected to stream yet.");
            }
        }

        public Task<int> GetReceivedCount()
        {
            int numReceived = _observer.NumItems;
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("ReceivedCount={0}", numReceived);
            }
            return Task.FromResult(numReceived);
        }
        public Task<int> GetErrorsCount()
        {
            int numErrors = _observer.NumErrors;
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("ErrorsCount={0}", numErrors);
            }
            return Task.FromResult(numErrors);
        }

        public Task Ping()
        {
            return TaskDone.Done;
        }

        public virtual async Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string providerName)
        {
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("BecomeConsumer StreamId={0} StreamProvider={1} Grain={2}", streamId, providerName,
                    this.AsReference());
            }
            InitStream(streamId, providerName);
            if (_observer == null)
            {
                _observer = new MyStreamObserver<int>(logger);
            }
            else
            {
                _observer.Reset();
            }
            StreamSubscriptionHandle<int> subsHandle = await State.Stream.SubscribeAsync(_observer);
            State.ConsumerSubscriptionHandles.Add(subsHandle);
#if USE_STORAGE
            await State.WriteStateAsync();
#endif
            return subsHandle;
        }

        public async Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<int> subsHandle)
        {
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("RemoveConsumer StreamId={0} StreamProvider={1}", streamId, providerName);
            }
            if (State.ConsumerSubscriptionHandles.Count == 0) throw new InvalidOperationException("Not a Consumer");
            await State.Stream.UnsubscribeAsync(subsHandle);
            _observer.Reset();
            State.ConsumerSubscriptionHandles.Remove(subsHandle);
#if USE_STORAGE
            await State.WriteStateAsync();
#endif
        }

#if USE_STORAGE
        async
#endif
        public Task ClearGrain()
        {
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("ClearGrain");
            }
            State.Stream = null;
            State.ConsumerSubscriptionHandles.Clear();
            State.IsProducer = false;
            _observer = null;
#if USE_STORAGE
            await State.ClearStateAsync();
#else
            return TaskDone.Done;
#endif
        }
    }

#if USE_STORAGE
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
#endif
    public class FilteredStreamConsumerGrain : StreamConsumerGrain, IFilteredStreamConsumerGrain
    {
        private static Logger _logger;

        private static readonly Int32 FilterDataOdd = 1;
        private static readonly Int32 FilterDataEven = 2;

        public override Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string providerName)
        {
            throw new InvalidOperationException("Should not be calling unfiltered BecomeConsumer method on " + GetType());
        }
        public async Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string providerName, bool sendEvensOnly)
        {
            _logger = logger;
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("BecomeConsumer StreamId={0} StreamProvider={1} Filter={2} Grain={3}",
                    streamId, providerName, sendEvensOnly, this.AsReference());
            }
            InitStream(streamId, providerName);
            if (_observer == null)
            {
                _observer = new MyStreamObserver<int>(logger);
            }
            else
            {
                _observer.Reset();
            }
            
            StreamFilterPredicate filterFunc;
            object filterData;
            if (sendEvensOnly)
            {
                filterFunc = FilterIsEven;
                filterData = FilterDataEven;
            }
            else
            {
                filterFunc = FilterIsOdd;
                filterData = FilterDataOdd;
            } 
            
            var subsHandle = await State.Stream.SubscribeAsync(_observer, null, filterFunc, filterData);

            State.ConsumerSubscriptionHandles.Add(subsHandle);
#if USE_STORAGE
            await State.WriteStateAsync();
#endif
            return subsHandle;
        }

        public static bool FilterIsEven(IStreamIdentity stream, object filterData, object item)
        {
            if (!filterData.Equals(FilterDataEven))
                throw new ArgumentException("Incorrect filter data object passed to filter function", "filterData");
            Int32 val = (int) item;
            bool result = val % 2 == 0;
            if (_logger != null)
            {
                _logger.Info("FilterIsEven(Stream={0},FilterData={1},Item={2})={3}", stream, filterData, item, result);
            }
            return result;
        }
        public static bool FilterIsOdd(IStreamIdentity stream, object filterData, object item)
        {
            if (!filterData.Equals(FilterDataOdd))
                throw new ArgumentException("Incorrect filter data object passed to filter function", "filterData");
            Int32 val = (int) item;
            bool result = val % 2 == 1;
            if (_logger != null) _logger.Info("FilterIsOdd(Stream={0},FilterData={1},Item={2})={3}", stream, filterData, item, result);
            return result;
        }
    }

#if USE_STORAGE
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
#endif
    public class StreamProducerGrain : StreamPubSubTestGrainBase, IStreamProducerGrain
    {
        public override Task OnActivateAsync()
        {
            logger = GetLogger(GetType().Name + "-" + IdentityString);
            if (logger.IsVerbose) logger.Info("OnActivateAsync");

            if (State.Stream != null && State.StreamProviderName != null)
            {
                if (logger.IsVerbose) logger.Info("Reconnecting to stream {0}", State.Stream);
            }
            else
            {
                if (logger.IsVerbose) logger.Info("Not connected to stream yet.");
            }
            return TaskDone.Done;
        }

        public Task<int> GetSendCount()
        {
            return Task.FromResult(State.NumMessagesSent);
        }

        public Task<int> GetErrorsCount()
        {
            return Task.FromResult(State.NumErrors);
        }

        public Task Ping()
        {
            return TaskDone.Done;
        }

        public async Task SendItem(int item)
        {
            if (!State.IsProducer || State.Stream == null) throw new InvalidOperationException("Not a Producer");
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("SendItem Item={0}", item);
            }
            Exception error = null;
            try
            {
                await State.Stream.OnNextAsync(item);

                if (loggingFilter == null || loggingFilter.ShouldLog())
                {
                    logger.Info("Successful SendItem " + item);
                }
                State.NumMessagesSent++;
            }
            catch (Exception exc)
            {
                logger.Error(0, "Error from SendItem " + item, exc);
                State.NumErrors++;
                error = exc;
            }
#if USE_STORAGE
            await State.WriteStateAsync(); // Update counts in persisted state
#endif

            if (error != null)
            {
                throw new AggregateException(error);
            }
        }

        public async Task BecomeProducer(Guid streamId, string providerName)
        {
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("BecomeProducer StreamId={0} StreamProvider={1}", streamId, providerName);
            }
            InitStream(streamId, providerName);
            State.IsProducer = true;

            // Send an initial message to ensure we are properly initialized as a Producer.
            await State.Stream.OnNextAsync(0);
            State.NumMessagesSent++;

#if USE_STORAGE
            await State.WriteStateAsync();
#endif
        }

#if USE_STORAGE
        async
#endif
        public Task ClearGrain()
        {
            if (loggingFilter == null || loggingFilter.ShouldLog())
            {
                logger.Info("ClearGrain");
            }
            State.IsProducer = false;
            State.Stream = null;
#if USE_STORAGE
            await State.ClearStateAsync();
#else
            return TaskDone.Done;
#endif
        }
    }

    [Serializable]
    public class MyStreamObserver<T> : IAsyncObserver<T>
    {
        internal int NumItems { get; private set; }
        internal int NumErrors { get; private set; }

        private readonly Logger logger;

        internal MyStreamObserver(Logger logger)
        {
            this.logger = logger;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token)
        {
            NumItems++;
            if (logger.IsVerbose)
                logger.Verbose("Received OnNextAsync - Item={0} - Total Items={1} Errors={2}", item, NumItems, NumErrors);
            return TaskDone.Done;
        }

        public Task OnCompletedAsync()
        {
            logger.Info("Receive OnCompletedAsync - Total Items={0} Errors={1}", NumItems, NumErrors);
            return TaskDone.Done;
        }

        public Task OnErrorAsync(Exception ex)
        {
            NumErrors++;
            logger.Warn(1, "Received OnErrorAsync - Exception={0} - Total Items={1} Errors={2}", ex, NumItems, NumErrors);
            return TaskDone.Done;
        }

        public void Reset()
        {
            NumErrors = 0;
            NumItems = 0;
        }
    }
}