
namespace Orleans.Runtime
{
    public struct SiloStatusChange
    {
        public SiloStatusChange(SiloAddress updatedSilo, SiloStatus status)
        {
            this.UpdatedSilo = updatedSilo;
            this.Status = status;
        }

        public SiloAddress UpdatedSilo { get; }
        public SiloStatus Status { get; }
    }

    public static class SiloStatusSharedState
    {
        public static ISharedState<SiloStatusChange> CreateSiloStatusSharedState(this ISiloStatusOracle oracle)
        {
            var currentStatus = new SiloStatusChange(oracle.SiloAddress, oracle.CurrentStatus);
            var listener = new SharedSiloStatusListener(currentStatus);
            oracle.SubscribeToSiloStatusEvents(listener);
            return listener.State;
        }

        private class SharedSiloStatusListener : SharedStatePublisherBase<SiloStatusChange>, ISiloStatusListener
        {
            public SharedState<SiloStatusChange> State => base.currentState;

            public SharedSiloStatusListener(SiloStatusChange startingState) : base(startingState)
            {
            }

            public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
            {
                this.Publish(new SiloStatusChange(updatedSilo, status));
            }
        }
    }
}
