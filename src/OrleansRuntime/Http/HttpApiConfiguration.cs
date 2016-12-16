namespace Orleans.Runtime
{
    internal struct HttpApiConfiguration
    {
        public bool Enable { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
