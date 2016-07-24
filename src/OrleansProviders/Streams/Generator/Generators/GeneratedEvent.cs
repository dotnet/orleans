using System;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Event use in generated streams
    /// </summary>
    [Serializable]
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
        public GeneratedEventType EventType { get; set; }

        /// <summary>
        /// Event payload
        /// </summary>
        public int[] Payload { get; set; }
    }
}
