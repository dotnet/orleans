using System;

namespace Orleans.Runtime
{
    // used for tracking request invocation history for deadlock detection.
    [Serializable]
    [GenerateSerializer]
    internal sealed class RequestInvocationHistory
    {
        [Id(1)]
        public GrainId GrainId { get; private set; }
        [Id(2)]
        public ActivationId ActivationId { get; private set; }

        [Obsolete("Removed and unused. This member is retained only for serialization compatibility purposes.")]
        [Id(3)]
        public string DebugContext { get; private set; }

        public RequestInvocationHistory(GrainId grainId, ActivationId activationId)
        {
            this.GrainId = grainId;
            this.ActivationId = activationId;
        }

        public override string ToString() => $"RequestInvocationHistory {GrainId}:{ActivationId}";
    }
}
