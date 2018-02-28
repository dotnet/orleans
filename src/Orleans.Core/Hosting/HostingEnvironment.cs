namespace Orleans.Hosting
{
    internal class HostingEnvironment : IHostingEnvironment
    {
        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; }
    }
}