namespace Orleans.Providers.GCP
{
    internal enum GoogleErrorCode
    {
        GoogleErrorCodeBase = 1 << 24,
        Initializing = GoogleErrorCodeBase + 1,
        DeleteTopic = GoogleErrorCodeBase + 2,
        PublishMessage = GoogleErrorCodeBase + 3,
        GetMessages = GoogleErrorCodeBase + 4,
        DeleteMessage = GoogleErrorCodeBase + 5,
        AcknowledgeMessage = GoogleErrorCodeBase + 6
    }
}
