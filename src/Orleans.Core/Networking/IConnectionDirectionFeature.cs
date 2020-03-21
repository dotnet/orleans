namespace Orleans.Runtime.Messaging
{
    public interface IConnectionDirectionFeature
    {
        bool IsOutboundConnection { get; }
    }
}
