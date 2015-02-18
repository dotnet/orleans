using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace OrleansProviders.PersistentStream.MockQueueAdapter
{
    internal class MockQueueAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly IMockQueueAdapterSettings _settings;
        private readonly IMockQueueAdapterBatchGenerator _generator;
        private readonly IMockQueueAdapterMonitor _monitor;
        private readonly BackPressureRegulator _backPressureRegulator;
        private readonly SimpleQueueAdapterCache _cache;
        //private long _cacheHighSequenceNumber = -1;
        private long _readerSequenceNumber;
        private int _batchesSentPerSecond;

        public QueueId Id { get; private set; }

        public static IQueueAdapterReceiver Create(QueueId queueId, IMockQueueAdapterSettings settings, IMockQueueAdapterBatchGenerator generator, IMockQueueAdapterMonitor monitor)
        {
            if (queueId == null)
            {
                throw new ArgumentNullException("queueId");
            }
            if (settings == null)
            {
                throw new ArgumentException("settings");
            }
            if (generator == null)
            {
                throw new ArgumentException("generator");
            }
            if (monitor == null)
            {
                throw new ArgumentException("monitor");
            }

            return new MockQueueAdapterReceiver(queueId, settings, generator, monitor);
        }

        private MockQueueAdapterReceiver(QueueId queueId, IMockQueueAdapterSettings settings, IMockQueueAdapterBatchGenerator generator, IMockQueueAdapterMonitor monitor)
        {
            Id = queueId;
            _settings = settings;
            _generator = generator;
            _monitor = monitor;
            // Consider: backpressure should be accumulated over time not event count.
            // Consider: high and low backpressure markers should be configurable
            _backPressureRegulator = new BackPressureRegulator(10 * _settings.TargetBatchesSentPerSecond, .005, 0.02);
            _cache = new SimpleQueueAdapterCache(settings.CacheSizeKb * (1 << 10));
            _batchesSentPerSecond = _settings.TargetBatchesSentPerSecond;

            _monitor.ReceiverCreated(queueId);
        }

        public Task Initialize(TimeSpan timeout)
        {
            return TaskDone.Done;
        }

        public async Task<IEnumerable<IBatchContainer>> GetQueueMessagesAsync()
        {
            // upper cap at twice the target rate
            if (_backPressureRegulator.Increase && _batchesSentPerSecond < 2*_settings.TargetBatchesSentPerSecond)
            {
                _batchesSentPerSecond++;
                _backPressureRegulator.Reset();
                _monitor.LowBackPressure(Id, _batchesSentPerSecond, _backPressureRegulator.BackPressure);
            } // lower cap at 1
            else if (_backPressureRegulator.Decrease && _batchesSentPerSecond > 1)
            {
                _batchesSentPerSecond--;
                _backPressureRegulator.Reset();
                _monitor.HighBackPressure(Id, _batchesSentPerSecond, _backPressureRegulator.BackPressure);
            }
            List<MockQueueAdapterBatchContainer> messages = (await _generator.GetQueueMessagesAsync(_batchesSentPerSecond)).ToList();

            // assign sequence Ids
            messages.ForEach(m => m.EventSequenceToken = new EventSequenceToken(_readerSequenceNumber++) );

            return messages;
        }

        public void AddToCache(IEnumerable<IBatchContainer> messages)
        {
            if (messages == null)
            {
                throw new ArgumentNullException("messages");
            }

            var batchContainers = messages as IList<IBatchContainer> ?? messages.ToList();
            _monitor.AddToCache(batchContainers.Count());
            foreach (MockQueueAdapterBatchContainer message in batchContainers.Cast<MockQueueAdapterBatchContainer>())
            {
                // get most recent sequenceId;
                // Why +1 again?
                //_cacheHighSequenceNumber = Math.Max(_cacheHighSequenceNumber, message.SequenceId + 1);

                _cache.Add(message, message.EventSequenceToken);
            }
        }


        public IQueueAdapterCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token)
        {
            EventSequenceToken sequenceToken;
            if (token == null)
            {
                // Null token can come from stream subscriber, if he is just interesesd to start consuming from latest.
                sequenceToken = new EventSequenceToken(_readerSequenceNumber);
            }
            else
            {
                var eventToken = token as EventSequenceToken;
                if (eventToken == null)
                {
                    throw new ArgumentOutOfRangeException("token", "token must be of type EventSequenceToken");
                }
                sequenceToken = eventToken;
            }
            return new StreamMesssageCursor(_cache, _monitor, _backPressureRegulator, streamGuid, streamNamespace, sequenceToken);
        }

        public Task Shutdown(TimeSpan timeout)
        {
            return TaskDone.Done;
        }

        private class StreamMesssageCursor : IQueueAdapterCacheCursor
        {
            private readonly SimpleQueueAdapterCache _cache;
            private readonly Guid _streamGuid;
            private readonly string _streamNamespace;
            private readonly IMockQueueAdapterMonitor _monitor;
            private readonly BackPressureRegulator _backPressureRegulator;
            private readonly object _cursor;
            private IBatchContainer _current;

            public StreamMesssageCursor(SimpleQueueAdapterCache cache, IMockQueueAdapterMonitor monitor,
                BackPressureRegulator backPressureRegulator, Guid streamGuid, string streamNamespace, EventSequenceToken sequenceId)
            {
                if (cache == null)
                {
                    throw new ArgumentNullException("cache");
                }
                if (monitor == null)
                {
                    throw new ArgumentNullException("monitor");
                }
                if (backPressureRegulator == null)
                {
                    throw new ArgumentNullException("backPressureRegulator");
                }
                _cache = cache;
                _streamGuid = streamGuid;
                _streamNamespace = streamNamespace;
                _monitor = monitor;
                _backPressureRegulator = backPressureRegulator;
                _cursor = _cache.GetCursor(sequenceId);
                _monitor.NewCursor(streamGuid, streamNamespace);
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                _monitor.BatchDeliveredToConsumer(_streamGuid, _streamNamespace);
                exception = null;
                return _current;
            }

            public bool MoveNext()
            {
                IBatchContainer next;
                double backPressure;
                while (_cache.TryGetNextMessage(_cursor, out next, out backPressure) && !IsInStream(next)) { }
                _backPressureRegulator.ReportBackPressure(backPressure);
                if (!IsInStream(next))
                {
                    return false;
                }
                _current = next;
                return true;
            }

            private bool IsInStream(IBatchContainer batchContainer)
            {
                return batchContainer != null &&
                       batchContainer.StreamGuid == _streamGuid &&
                       batchContainer.StreamNamespace == _streamNamespace;
            }
        }

        private class BackPressureRegulator
        {
            private BackPressureState _state = BackPressureState.None;
            private readonly double[] _votes;
            private int _voteIndex;
            private readonly double _highPressure;
            private readonly double _lowPressure;

            public double BackPressure { get; private set; }

            private enum BackPressureState
            {
                Low,
                High,
                None,
            }

            public bool Increase
            {
                get { return _state == BackPressureState.Low; }
            }

            public bool Decrease
            {
                get { return _state == BackPressureState.High; }
            }

            public BackPressureRegulator(int voteCount, double lowPressure, double highPressure)
            {
                if (lowPressure > 1 || lowPressure < 0)
                {
                    throw new ArgumentOutOfRangeException("lowPressure");
                }
                if (highPressure > 1 || highPressure < 0)
                {
                    throw new ArgumentOutOfRangeException("highPressure");
                }
                _lowPressure = lowPressure;
                _highPressure = highPressure;
                _votes = new double[voteCount];   
            }

            public void ReportBackPressure(double backPressure)
            {
                _votes[_voteIndex++] = backPressure;
                _voteIndex = _voteIndex % _votes.Length;
                if (_voteIndex == 0)
                {
                    BackPressure = _votes.Average();
                    if (BackPressure >= _highPressure)
                    {
                        _state = BackPressureState.High;
                    }
                    else if (BackPressure <= _lowPressure)
                    {
                        _state = BackPressureState.Low;
                    }
                    else
                    {
                        _state = BackPressureState.None;
                    }
                }
            }

            public void Reset()
            {
                _state = BackPressureState.None;
            }
        }
    }
}