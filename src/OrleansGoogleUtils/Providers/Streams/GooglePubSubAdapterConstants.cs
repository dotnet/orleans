﻿namespace Orleans.Serialization.Providers.Streams
{
    public static class GooglePubSubAdapterConstants
    {
        public const string NUMBER_SUBSCRIPTIONS = "NumSubscriptions";
        public const int NUMBER_SUBSCRIPTIONS_DEFAULT = 8;
        internal const int CACHE_SIZE_DEFAULT = 4096;
        public const string PROJECT_ID = "ProjectId";
        public const string TOPIC_ID = "TopicId";
        public const string DEADLINE = "Deadline";
        public const string DEPLOYMENT_ID = "DeploymentId";
    }
}
