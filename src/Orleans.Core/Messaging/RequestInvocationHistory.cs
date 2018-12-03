using System;

namespace Orleans.Runtime
{
    // used for tracking request invocation history for deadlock detection.
    [Serializable]
    internal sealed class RequestInvocationHistory : RequestInvocationHistorySummary
    {
        public GrainId GrainId { get; private set; }
        public string DebugContext { get; private set; }

        public RequestInvocationHistory(GrainId grainId, ActivationId activationId, string debugContext) : base(activationId)
        {
            this.GrainId = grainId;
            DebugContext = debugContext;
        }

        public override string ToString()
        {
            return $"RequestInvocationHistory {GrainId}:{ActivationId}:{DebugContext}";
        }
    }
    // used for tracking request invocation history for call chain reentrancy
    [Serializable]
    internal class RequestInvocationHistorySummary
    {
        public ActivationId ActivationId { get; private set; }

        public RequestInvocationHistorySummary(ActivationId activationId)
        {
            this.ActivationId = activationId;
        }

        public override string ToString()
        {
            return $"RequestInvocationHistorySummary {ActivationId}";
        }
    }
}
