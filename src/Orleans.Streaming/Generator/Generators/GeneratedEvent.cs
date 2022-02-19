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
            /// Filler event
            /// </summary>
            Fill,

            /// <summary>
            /// Event that should trigger reporting
            /// </summary>
            Report
        }

        /// <summary>
        /// Gets or sets the event type.
        /// </summary>
        [Id(0)]
        public GeneratedEventType EventType { get; set; }

        /// <summary>
        /// Gets or sets the payload.
        /// </summary>
        [Id(1)]
        public int[] Payload { get; set; }
    }
}
