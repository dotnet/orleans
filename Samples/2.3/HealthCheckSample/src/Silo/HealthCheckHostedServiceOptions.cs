namespace Silo
{
    public class HealthCheckHostedServiceOptions
    {
        public string PathString { get; set; } = "/health";
        public int Port { get; set; } = 8880;
    }
}
