namespace Orleans.Providers.Streams
{
    public enum GoogleErrorCode
    {
        GoogleErrorCodeBase = ErrorCode.Runtime + 4500,
        Initializing = GoogleErrorCodeBase + 1,
        DeleteTopic = GoogleErrorCodeBase + 2,
        PublishMessage = GoogleErrorCodeBase + 3,
        GetMessages = GoogleErrorCodeBase + 4,
        DeleteMessage = GoogleErrorCodeBase + 5,
        AcknowledgeMessage = GoogleErrorCodeBase + 6
    }
}
