namespace Orleans.Client.Hosting
{
    public class NamedOrleansHostedClientBuilder
    {
        public string Name { get; set; }
        public IClientBuilder ClientBuilder { get; set; }
    }
}
