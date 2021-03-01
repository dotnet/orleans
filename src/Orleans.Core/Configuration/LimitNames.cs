namespace Orleans.Runtime.Configuration
{

    /// <summary>
    /// Class containing key names for the configurable LimitValues used by Orleans runtime.
    /// </summary>
    internal static class LimitNames
    {
        public const string LIMIT_MAX_ENQUEUED_REQUESTS = "MaxEnqueuedRequests";
        public const string LIMIT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER = "MaxEnqueuedRequests_StatelessWorker";
    }
}
