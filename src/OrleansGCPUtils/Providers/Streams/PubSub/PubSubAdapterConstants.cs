using System;

namespace Orleans.Providers.GCP.Streams.PubSub
{
    public static class PubSubAdapterConstants
    {
        internal static readonly TimeSpan MAX_DEADLINE = TimeSpan.FromSeconds(600);
        internal const string NUMBER_SUBSCRIPTIONS = "NumSubscriptions";
        internal const int NUMBER_SUBSCRIPTIONS_DEFAULT = 8;
        internal const int CACHE_SIZE_DEFAULT = 4096;
        internal const string PROJECT_ID = "ProjectId";
        internal const string TOPIC_ID = "TopicId";
        internal const string DEADLINE = "Deadline";
        internal const string DEPLOYMENT_ID = "DeploymentId";
        internal const string CUSTOM_ENDPOINT = "CustomEndpoint";
    }
}
