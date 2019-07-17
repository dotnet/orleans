namespace Orleans.Client.Hosting
{
    public interface IOrleansHostedClientAccessor
    {
        IClusterClient Client { get; }
        IClusterClient GetClient(string name);
    }
}
