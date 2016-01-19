using System;

namespace TestGrains
{
    [Serializable]
    public class GeneratedEvent
    {
        public enum GeneratedEventType
        {
            Fill,
            End,
        }

        public GeneratedEventType EventType { get; set; }
    }
}
