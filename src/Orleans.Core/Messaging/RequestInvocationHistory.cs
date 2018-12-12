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

        public RequestInvocationHistory(GrainId grainId, ActivationId activationId, string debugContext)
        {
            this.GrainId = grainId;
            this.ActivationId = activationId;
            DebugContext = debugContext;
        }

        public override string ToString()
        {
            return String.Format("RequestInvocationHistory {0}:{1}:{2}", GrainId, ActivationId, DebugContext);
        }
    }
}
