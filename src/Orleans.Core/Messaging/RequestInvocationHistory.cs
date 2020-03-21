using System;

namespace Orleans.Runtime
{
    // used for tracking request invocation history for deadlock detection.
    [Serializable]
    internal sealed class RequestInvocationHistory
    {
        public GrainId GrainId { get; private set; }
        public ActivationId ActivationId { get; private set; }

        [Obsolete("Removed and unused. This member is retained only for serialization compatibility purposes.")]
        public string DebugContext { get; private set; }

        public RequestInvocationHistory(GrainId grainId, ActivationId activationId)
        {
            this.GrainId = grainId;
            this.ActivationId = activationId;
        }

        public override string ToString() => $"RequestInvocationHistory {GrainId}:{ActivationId}";
    }
}
