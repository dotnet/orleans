namespace Orleans.Runtime.Hosting
{
    internal class NamedService<TService>
    {
        public NamedService(string name, TService service)
        {
            Name= name;
            Service = service;
        }

        public string Name { get; }

        public TService Service { get; }
    }
}
