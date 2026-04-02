using System.Diagnostics;
using Orleans.Diagnostics;

#nullable disable
namespace Orleans.Streams
{
    internal sealed partial class PersistentStreamPullingAgent
    {
        private static readonly DiagnosticListener DiagnosticListener = new(OrleansStreamingDiagnostics.ListenerName);

        private void EmitMessageDeliveredDiagnostics(StreamConsumerData consumerData, IBatchContainer batch)
        {
            if (!DiagnosticListener.IsEnabled(OrleansStreamingDiagnostics.EventNames.MessageDelivered))
            {
                return;
            }

            DiagnosticListener.Write(
                OrleansStreamingDiagnostics.EventNames.MessageDelivered,
                new StreamMessageDeliveredEvent(
                    streamProviderName,
                    consumerData.StreamId.StreamId,
                    consumerData.SubscriptionId.Guid,
                    consumerData.StreamConsumer.GetGrainId(),
                    batch.SequenceToken?.ToString(),
                    Silo));
        }

        private void EmitStreamInactiveDiagnostics(StreamId streamId)
        {
            if (!DiagnosticListener.IsEnabled(OrleansStreamingDiagnostics.EventNames.StreamInactive))
            {
                return;
            }

            DiagnosticListener.Write(
                OrleansStreamingDiagnostics.EventNames.StreamInactive,
                new StreamInactiveEvent(
                    streamProviderName,
                    streamId,
                    options.StreamInactivityPeriod,
                    Silo));
        }
    }
}
