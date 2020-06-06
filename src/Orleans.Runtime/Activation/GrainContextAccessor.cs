namespace Orleans.Runtime
{
    internal class GrainContextAccessor : IGrainContextAccessor
    {
        private HostedClient _hostedClient;

        public GrainContextAccessor(HostedClient hostedClient)
        {
            _hostedClient = hostedClient;
        }

        public IGrainContext GrainContext => RuntimeContext.CurrentGrainContext ?? _hostedClient;
    }
}
