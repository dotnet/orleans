using System;

namespace Orleans.Runtime
{
    // used for tracking request invocation history for deadlock detection.
    [Serializable]
    internal sealed class RequestInvocationHistory
    {
        public GrainId GrainId { get; private set; }
        public ActivationId ActivationId { get; private set; }
        public string DebugContext { get; private set; }

        internal RequestInvocationHistory(Message message)
        {
            GrainId = message.TargetGrain;
            ActivationId = message.TargetActivation;
            DebugContext = message.DebugContext;
        }

        public override string ToString()
        {
            return String.Format("RequestInvocationHistory {0}:{1}:{2}", GrainId, ActivationId, DebugContext);
        }
    }
}
