namespace Orleans.Runtime
{
    internal class GrainContextAccessor : IGrainContextAccessor
    {
        private readonly HostedClient _hostedClient;

        public GrainContextAccessor(HostedClient hostedClient)
        {
            _hostedClient = hostedClient;
        }

        public IGrainContext GrainContext => RuntimeContext.Current ?? _hostedClient;
    }
}
