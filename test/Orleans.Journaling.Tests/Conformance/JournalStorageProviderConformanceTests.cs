using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class VolatileJournalStorageConformanceTests : JournalStorageConformanceTests
{
    private readonly VolatileJournalStorageProvider _provider = new();

    protected override ValueTask<IJournalStorageProvider> CreateProviderAsync(JournalStorageFaultInjector? faults, CancellationToken cancellationToken)
        => new(_provider);
}

[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class AzureBlobJournalStorageConformanceTests : JournalStorageConformanceTests
{
    private readonly string _containerName = $"conformance-{Guid.NewGuid():N}";
    private readonly ConformanceProviderHost _host = new();
    private BlobServiceClient? _client;

    public AzureBlobJournalStorageConformanceTests()
    {
        JournalingAzureStorageTestConfiguration.CheckPreconditionsOrThrow();
    }

    protected override long? MaxAppendBytes => AzureBlobJournalStorage.MaxAppendBlockBytes;

    // Azurite stops serving append-block requests after a client aborts an append-block request body, so Blob conformance
    // injects lost responses only.
    protected override IReadOnlySet<JournalStorageFaultKind> SupportedFaults { get; } = new HashSet<JournalStorageFaultKind> { JournalStorageFaultKind.LoseResponse };

    protected override async ValueTask<IJournalStorageProvider> CreateProviderAsync(JournalStorageFaultInjector? faults, CancellationToken cancellationToken)
    {
        var options = new AzureBlobJournalStorageOptions { ContainerName = _containerName };
        options.ClientOptions = AzureFaultInjectionHandler.ConfigureClientOptions(new BlobClientOptions(), faults);
        options.ConfigureTestDefaults();
        _client ??= await options.CreateClient!(cancellationToken);
        var provider = new AzureBlobJournalStorageProvider(
            Options.Create(options),
            ConformanceProviderHost.ManagerOptions,
            _host.Services,
            NullLogger<AzureBlobJournalStorage>.Instance);
        await _host.StartAsync(provider, cancellationToken);
        return provider;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.GetBlobContainerClient(_containerName).DeleteIfExistsAsync();
        }

        await _host.DisposeAsync();
    }
}

[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class AzureTableJournalStorageConformanceTests : JournalStorageConformanceTests
{
    private readonly string _tableName = $"conformance{Guid.NewGuid():N}";
    private readonly ConformanceProviderHost _host = new();
    private TableServiceClient? _client;

    public AzureTableJournalStorageConformanceTests()
    {
        JournalingAzureStorageTestConfiguration.CheckPreconditionsOrThrow();
    }

    protected override long? MaxAppendBytes => AzureTableJournalStorage.MaxAppendBytes;

    protected override IReadOnlySet<JournalStorageFaultKind> SupportedFaults { get; } = new HashSet<JournalStorageFaultKind>
    {
        JournalStorageFaultKind.AbortRequestBody,
        JournalStorageFaultKind.LoseResponse,
    };

    protected override async ValueTask<IJournalStorageProvider> CreateProviderAsync(JournalStorageFaultInjector? faults, CancellationToken cancellationToken)
    {
        var options = new AzureTableJournalStorageOptions { TableName = _tableName };
        options.ClientOptions = AzureFaultInjectionHandler.ConfigureClientOptions(new TableClientOptions(), faults);
        options.ConfigureTestDefaults();
        _client ??= await options.CreateClient!(cancellationToken);
        var provider = new AzureTableJournalStorageProvider(
            Options.Create(options),
            ConformanceProviderHost.ManagerOptions,
            _host.Services,
            NullLogger<AzureTableJournalStorage>.Instance);
        await _host.StartAsync(provider, cancellationToken);
        return provider;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DeleteTableAsync(_tableName);
        }

        await _host.DisposeAsync();
    }
}

[TestSuite("BVT")]
[TestProvider("S3")]
[TestCategory("BVT")]
[TestCategory("AWS")]
[TestCategory("S3")]
public sealed class S3JournalStorageConformanceTests : JournalStorageConformanceTests
{
    private readonly ConformanceProviderHost _host = new();
    private readonly string _bucketName = $"journaling-conformance-{Guid.NewGuid():N}";
    private IContainer? _container;
    private AmazonS3Client? _client;

    public override async ValueTask InitializeAsync()
    {
        if (S3JournalStorageTests.GetDockerSkipReason() is { } reason)
        {
            Assert.Skip(reason);
        }

        _container = SeaweedFSTestContainer.Create();
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _client = new AmazonS3Client(
            new BasicAWSCredentials(SeaweedFSTestContainer.AccessKey, SeaweedFSTestContainer.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = $"http://127.0.0.1:{_container.GetMappedPublicPort(SeaweedFSTestContainer.Port)}",
                ForcePathStyle = true,
                AuthenticationRegion = RegionEndpoint.USEast1.SystemName,
            });
        await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucketName }, TestContext.Current.CancellationToken);
        await base.InitializeAsync();
    }

    protected override async ValueTask<IJournalStorageProvider> CreateProviderAsync(JournalStorageFaultInjector? faults, CancellationToken cancellationToken)
    {
        var provider = new S3JournalStorageProvider(
            Options.Create(new S3JournalStorageOptions
            {
                BucketName = _bucketName,
                S3Client = _client!,
                UseS3ExpressAppend = false,
                UseConditionalDelete = false,
                StorageClass = null,
                MetadataOnlyConflictInitialBackoff = TimeSpan.Zero,
            }),
            ConformanceProviderHost.ManagerOptions,
            _host.Services,
            NullLogger<S3JournalStorage>.Instance);
        await _host.StartAsync(provider, cancellationToken);
        return provider;
    }

    public override async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        await _host.DisposeAsync();
    }
}

// Hosts journal storage providers the way a silo does: with a journal format registered and the silo lifecycle started.
internal sealed class ConformanceProviderHost : IAsyncDisposable
{
    private readonly List<SiloLifecycleSubject> _lifecycles = [];

    public static IOptions<JournaledStateManagerOptions> ManagerOptions { get; } = Options.Create(new JournaledStateManagerOptions
    {
        JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey,
    });

    public ServiceProvider Services { get; } = CreateServices();

    public async Task StartAsync(ILifecycleParticipant<ISiloLifecycle> provider, CancellationToken cancellationToken)
    {
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        provider.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);
        _lifecycles.Add(lifecycle);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lifecycle in _lifecycles)
        {
            await lifecycle.OnStop(CancellationToken.None);
        }

        await Services.DisposeAsync();
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        services.AddSingleton<OrleansBinaryJournalFormat>();
        services.AddKeyedSingleton<IJournalFormat>(
            OrleansBinaryJournalFormat.JournalFormatKey,
            static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        return services.BuildServiceProvider();
    }
}

// Injects journal storage faults into an Azure SDK client's write requests at the transport, inside the client's retry loop.
internal sealed class AzureFaultInjectionHandler(JournalStorageFaultInjector faults) : DelegatingHandler(new SocketsHttpHandler())
{
    public static TOptions ConfigureClientOptions<TOptions>(TOptions options, JournalStorageFaultInjector? faults)
        where TOptions : ClientOptions
    {
        if (faults is not null)
        {
            options.Transport = new HttpClientTransport(new HttpClient(new AzureFaultInjectionHandler(faults)));
            options.Retry.Mode = RetryMode.Fixed;
            options.Retry.Delay = TimeSpan.FromMilliseconds(10);
            options.Retry.MaxRetries = 3;
        }

        return options;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        if (request.Content is not null && faults.TryTake(JournalStorageFaultKind.AbortRequestBody))
        {
            await SendPartialRequestAsync(request, cancellationToken);
            throw new HttpRequestException("Injected fault: the connection dropped while sending the request body.", new IOException());
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (faults.TryTake(JournalStorageFaultKind.LoseResponse))
        {
            response.Dispose();
            throw new HttpRequestException("Injected fault: the response to an applied storage request was lost.", new IOException());
        }

        return response;
    }

    // Sends the request headers and the first half of its body on a dedicated connection, then resets the connection.
    private static async Task SendPartialRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        var uri = request.RequestUri!;
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{request.Method.Method} {uri.PathAndQuery} HTTP/1.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"Host: {uri.Authority}\r\n")
            .Append(request.Headers.ToString().Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal))
            .Append(request.Content.Headers.ToString().Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal));
        if (request.Content.Headers.ContentLength is null)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        }

        head.Append("\r\n");

        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port, cancellationToken);
        client.LingerState = new LingerOption(enable: true, seconds: 0);
        Stream stream = client.GetStream();
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            var ssl = new SslStream(stream);
            await ssl.AuthenticateAsClientAsync(uri.Host);
            stream = ssl;
        }

        await using (stream)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken);
            await stream.WriteAsync(body.AsMemory(0, body.Length / 2), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
}