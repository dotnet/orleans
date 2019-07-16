namespace Orleans.Client.Hosting
{
    public interface IOrleansClientAccessor
    {
        IClusterClient Client { get; }
    }
}
