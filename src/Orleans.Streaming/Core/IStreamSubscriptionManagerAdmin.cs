namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionManagerAdmin
    {
        IStreamSubscriptionManager GetStreamSubscriptionManager(string managerType);
    }

    public static class StreamSubscriptionManagerType
    {
        public const string ExplicitSubscribeOnly = "ExplicitSubscribeOnly";
    }
}
