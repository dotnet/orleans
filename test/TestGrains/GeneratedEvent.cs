using System;

namespace TestGrains
{
    [Serializable]
    public class GeneratedEvent
    {
        public enum GeneratedEventType
        {
            Fill,
            Report,
        }

        public GeneratedEventType EventType { get; set; }
        public int[] Payload { get; set; }
    }
}
