using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Orleans.Journaling.Tests;

internal static class SeaweedFSTestContainer
{
    internal const int Port = 8333;
    internal const string AccessKey = "orleans-test";
    internal const string SecretKey = "orleans-test-secret";

    // Keep in sync with SEAWEEDFS_IMAGE in .github/workflows/ci.yml.
    private const string Image = "ghcr.io/chrislusf/seaweedfs:4.46@sha256:08d516132314207d10c8e37cbffc1f32b147d870169688734cc61c6231625b62";

    internal static IContainer Create() =>
        new ContainerBuilder(Image)
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
            .WithCommand(
                "mini",
                "-dir=/data",
                "-ip=127.0.0.1",
                "-ip.bind=0.0.0.0",
                "-master.telemetry=false",
                "-admin.ui=false",
                "-webdav=false",
                "-s3.port.iceberg=0",
                "-s3.port.lance=0")
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(Port)
                .ForPath("/readyz")))
            .Build();
}
