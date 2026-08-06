using Testcontainers.Consul;
using TestExtensions;

namespace Consul.Tests
{
    /// <summary>
    /// Utility class for Consul test setup and connection verification.
    /// </summary>
    public static class ConsulTestUtils
    {
        private static readonly TestcontainerManager<ConsulContainer> ContainerManager = new("Consul", CreateContainer);

        public static string ConsulConnectionString
        {
            get
            {
                return ContainerManager.Container.GetBaseAddress();
            }
        }

        public static void EnsureConsul()
        {
            ContainerManager.EnsureStarted();
        }

        public static Task<bool> EnsureConsulAsync()
        {
            return ContainerManager.EnsureStartedAsync();
        }

        private static ConsulContainer CreateContainer()
        {
            return new ConsulBuilder("public.ecr.aws/hashicorp/consul:1.19")
                .WithCreateParameterModifier(parameters =>
                {
                    if (parameters.HostConfig is not null)
                    {
                        parameters.HostConfig.CapAdd = ["IPC_LOCK"];
                    }
                })
                .Build();
        }
    }
}
