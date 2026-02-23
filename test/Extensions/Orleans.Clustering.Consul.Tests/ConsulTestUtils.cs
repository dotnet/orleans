using Docker.DotNet;
using DotNet.Testcontainers.Configurations;
using Testcontainers.Consul;
using Xunit;

namespace Consul.Tests
{
    /// <summary>
    /// Utility class for Consul test setup and connection verification.
    /// </summary>
    public static class ConsulTestUtils
    {
        private const string DockerUnavailableSkipReason = "Docker is unavailable, so Consul tests are skipped.";

        private const string WindowsDockerModeSkipReason = "Docker is running in Windows container mode (OSType=windows), so Consul tests are skipped.";

        private static readonly Lazy<string> DockerDaemonOsTypeLazy = new(GetDockerDaemonOsType);

        private static readonly Lazy<string> EnsureConsulSkipReasonLazy = new(() => EnsureConsulAndGetSkipReasonAsync().GetAwaiter().GetResult());

        private static readonly ConsulContainer _container = new ConsulBuilder("public.ecr.aws/hashicorp/consul:1.19")
            .WithCreateParameterModifier(parameters =>
            {
                if (parameters.HostConfig is not null && !IsWindowsDockerDaemon())
                {
                    parameters.HostConfig.CapAdd = ["IPC_LOCK"];
                }
            })
            .Build();

        public static string ConsulConnectionString
        {
            get
            {
                EnsureConsul();
                return _container.GetBaseAddress();
            }
        }

        public static void EnsureConsul()
        {
            var skipReason = EnsureConsulSkipReasonLazy.Value;
            if (skipReason is not null)
                throw new SkipException(skipReason);
        }

        public static Task<bool> EnsureConsulAsync()
        {
            return Task.FromResult(EnsureConsulSkipReasonLazy.Value is null);
        }

        private static async Task<string> EnsureConsulAndGetSkipReasonAsync()
        {
            var skipReason = GetDockerSkipReason();
            if (skipReason is not null)
            {
                return skipReason;
            }

            try
            {
                await _container.StartAsync();
                return null;
            }
            catch (HttpRequestException)
            {
                return DockerUnavailableSkipReason;
            }
            catch (OperationCanceledException)
            {
                return DockerUnavailableSkipReason;
            }
        }

        private static string GetDockerSkipReason()
        {
            var dockerDaemonOsType = DockerDaemonOsTypeLazy.Value;
            if (string.IsNullOrWhiteSpace(dockerDaemonOsType))
            {
                return DockerUnavailableSkipReason;
            }

            if (string.Equals(dockerDaemonOsType, "windows", StringComparison.OrdinalIgnoreCase))
            {
                return WindowsDockerModeSkipReason;
            }

            return null;
        }

        private static bool IsWindowsDockerDaemon()
        {
            return string.Equals(DockerDaemonOsTypeLazy.Value, "windows", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDockerDaemonOsType()
        {
            try
            {
                using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig
                    .GetDockerClientConfiguration(Guid.NewGuid())
                    .CreateClient();
                var dockerInfo = dockerClient.System.GetSystemInfoAsync().GetAwaiter().GetResult();
                return dockerInfo.OSType;
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (DockerApiException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
