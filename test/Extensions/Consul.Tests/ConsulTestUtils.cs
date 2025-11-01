using Testcontainers.Consul;
using Xunit;

namespace Consul.Tests
{
    /// <summary>
    /// Utility class for Consul test setup and connection verification.
    /// </summary>
    public static class ConsulTestUtils
    {
        private static readonly ConsulContainer _container = new ConsulBuilder()
            .WithImage("hashicorp/consul:1.19")
            .Build();

        public static string ConsulConnectionString
        {
            get
            {
                EnsureConsul();
                return _container.GetBaseAddress();
            }
        }
        private static readonly Lazy<bool> EnsureConsulLazy = new Lazy<bool>(() => EnsureConsulAsync().Result);

        public static void EnsureConsul()
        {
            if (!EnsureConsulLazy.Value)
                throw new SkipException("Consul cluster isn't running");
        }

        public static async Task<bool> EnsureConsulAsync()
        {
            try
            {
                await _container.StartAsync();
                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
