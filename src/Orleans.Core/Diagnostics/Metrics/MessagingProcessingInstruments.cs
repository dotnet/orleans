using System;
using System.Diagnostics.Metrics;

#nullable disable
namespace Orleans.Runtime;

internal sealed class MessagingProcessingInstruments
{
    private readonly Meter _meter;
    private readonly CounterAggregatorGroup _dispatcherMessagesProcessedCounterAggregatorGroup = new();
    private readonly ObservableCounter<long> _dispatcherMessagesProcessedCounter;

    private readonly CounterAggregatorGroup _dispatcherMessagesReceivedCounterAggregatorGroup = new();
    private readonly ObservableCounter<long> _dispatcherMessagesReceivedCounter;

    private readonly CounterAggregator _dispatcherMessagesForwardedCounterAggregator = new();
    private readonly ObservableCounter<long> _dispatcherMessagesForwardedCounter;
    private readonly CounterAggregator _imaReceivedCounterAggregator = new();
    private readonly ObservableCounter<long> _imaReceivedCounter;
    private readonly CounterAggregatorGroup _imaEnqueuedCounterAggregatorGroup = new();
    private readonly ObservableCounter<long> _imaEnqueuedCounter;
    private readonly CounterAggregator _imaMessageEnqueuedNullContext;
    private readonly CounterAggregator _imaMessageEnqueuedSystemTarget;
    private readonly CounterAggregator _imaMessageEnqueuedGrain;
    private readonly CounterAggregator[] _dispatcherMessagesReceivedCountersNullContext;
    private readonly CounterAggregator[] _dispatcherMessagesReceivedCountersGrain;

    private readonly CounterAggregator[] _dispatcherMessagesProcessedCountersOk;
    private readonly CounterAggregator[] _dispatcherMessagesProcessedCountersError;
    private ObservableGauge<long> _activationDataAll;

    public MessagingProcessingInstruments(OrleansInstruments instruments)
    {
        _meter = instruments.Meter;
        _dispatcherMessagesProcessedCounter = _meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_PROCESSED, _dispatcherMessagesProcessedCounterAggregatorGroup.Collect);
        _dispatcherMessagesReceivedCounter = _meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_RECEIVED, _dispatcherMessagesReceivedCounterAggregatorGroup.Collect);
        _dispatcherMessagesForwardedCounter = _meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_FORWARDED, _dispatcherMessagesForwardedCounterAggregator.Collect);
        _imaReceivedCounter = _meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_IMA_RECEIVED, _imaReceivedCounterAggregator.Collect);
        _imaEnqueuedCounter = _meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_IMA_ENQUEUED, _imaEnqueuedCounterAggregatorGroup.Collect);

        _imaMessageEnqueuedNullContext = _imaEnqueuedCounterAggregatorGroup.FindOrCreate(new("Context", "ToNull"));
        _imaMessageEnqueuedSystemTarget = _imaEnqueuedCounterAggregatorGroup.FindOrCreate(new("Context", "ToSystemTarget"));
        _imaMessageEnqueuedGrain = _imaEnqueuedCounterAggregatorGroup.FindOrCreate(new("Context", "ToGrain"));
        var directionEnumValues = Enum.GetValues<Message.Directions>();
        _dispatcherMessagesReceivedCountersNullContext = new CounterAggregator[directionEnumValues.Length + 1];
        _dispatcherMessagesReceivedCountersGrain = new CounterAggregator[directionEnumValues.Length + 1];
        _dispatcherMessagesProcessedCountersOk = new CounterAggregator[directionEnumValues.Length + 1];
        _dispatcherMessagesProcessedCountersError = new CounterAggregator[directionEnumValues.Length + 1];
        foreach (var value in directionEnumValues)
        {
            _dispatcherMessagesReceivedCountersNullContext[(int)value] = _dispatcherMessagesReceivedCounterAggregatorGroup.FindOrCreate(new("Context", "None", "Direction", value.ToString()));
            _dispatcherMessagesReceivedCountersGrain[(int)value] = _dispatcherMessagesReceivedCounterAggregatorGroup.FindOrCreate(new("Context", "Grain", "Direction", value.ToString()));

            _dispatcherMessagesProcessedCountersOk[(int)value] = _dispatcherMessagesProcessedCounterAggregatorGroup.FindOrCreate(new("Direction", value.ToString(), "Status", "Ok"));
            _dispatcherMessagesProcessedCountersError[(int)value] = _dispatcherMessagesProcessedCounterAggregatorGroup.FindOrCreate(new("Direction", value.ToString(), "Status", "Error"));
        }
    }

    internal void OnDispatcherMessageReceive(Message msg)
    {
        if (!_dispatcherMessagesReceivedCounter.Enabled)
            return;
        var context = RuntimeContext.Current;
        var counters = context switch
        {
            null => _dispatcherMessagesReceivedCountersNullContext,
            _ => _dispatcherMessagesReceivedCountersGrain,
        };
        counters[(int)msg.Direction].Add(1);
    }

    internal void OnDispatcherMessageProcessedOk(Message msg)
    {
        if (_dispatcherMessagesProcessedCounter.Enabled)
        {
            _dispatcherMessagesProcessedCountersOk[(int)msg.Direction].Add(1);
        }
    }

    internal void OnDispatcherMessageProcessedError(Message msg)
    {
        if (_dispatcherMessagesProcessedCounter.Enabled)
        {
            _dispatcherMessagesProcessedCountersError[(int)msg.Direction].Add(1);
        }
    }

    internal void OnDispatcherMessageForwared(Message msg)
    {
        if (_dispatcherMessagesForwardedCounter.Enabled)
        {
            _dispatcherMessagesForwardedCounterAggregator.Add(1);
        }
    }

    internal void OnImaMessageReceived(Message msg)
    {
        if (_imaReceivedCounter.Enabled)
        {
            _imaReceivedCounterAggregator.Add(1);
        }
    }

    internal void OnImaMessageEnqueued(IGrainContext context)
    {
        if (!_imaEnqueuedCounter.Enabled)
        {
            return;
        }

        switch (context)
        {
            case null:
                _imaMessageEnqueuedNullContext.Add(1);
                break;
            case ISystemTargetBase:
                _imaMessageEnqueuedSystemTarget.Add(1);
                break;
            default:
                _imaMessageEnqueuedGrain.Add(1);
                break;
        }
    }

    internal void RegisterActivationDataAllObserve(Func<long> observeValue)
    {
        _activationDataAll = _meter.CreateObservableGauge(InstrumentNames.MESSAGING_PROCESSING_ACTIVATION_DATA_ALL, observeValue);
    }
}
