using System;

namespace Orleans.Runtime
{
    // used for tracking request invocation history for deadlock detection.
    [Serializable]
    internal sealed class RequestInvocationHistory
    {
        public GrainId GrainId { get; private set; }
        public ActivationId ActivationId { get; private set; }
        public int InterfaceId { get; private set; }
        public int MethodId { get; private set; }

        internal RequestInvocationHistory(Message message)
        {
            GrainId = message.TargetGrain;
            ActivationId = message.TargetActivation;
            InterfaceId = message.InterfaceId;
            MethodId = message.MethodId;
        }

        public override string ToString()
        {
            return String.Format("RequestInvocationHistory {0}:{1}:{2}:{3}", GrainId, ActivationId, InterfaceId, MethodId);
        }
    }
}
