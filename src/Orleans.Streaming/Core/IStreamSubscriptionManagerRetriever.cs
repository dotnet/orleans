namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionManagerRetriever
    {
        IStreamSubscriptionManager GetStreamSubscriptionManager();
    }
}
