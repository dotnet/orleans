using System;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Event use in generated streams
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class GeneratedEvent
    {
        /// <summary>
        /// Generated event type
        /// </summary>
        public enum GeneratedEventType
        {
            /// <summary>
            /// filler event
            /// </summary>
            Fill,
            /// <summary>
            /// Event that should trigger reporting
            /// </summary>
            Report
        }

        /// <summary>
        /// Event type
        /// </summary>
        [Id(0)]
        public GeneratedEventType EventType { get; set; }

        /// <summary>
        /// Event payload
        /// </summary>
        [Id(1)]
        public int[] Payload { get; set; }
    }
}
