using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Orleans.Tests.Google;

public class GoogleEmulatorNotAvailableException : Exception
{
    public GoogleEmulatorNotAvailableException(string message) : base(message) { }
}

/// <summary>
/// Manages the lifecycle of the Google Cloud Platform emulators.
/// </summary>
public class GoogleEmulatorHost : IAsyncDisposable
{
    private const int STORAGE_PORT = 9199;
    private const int PUBSUB_PORT = 8085;
    private const int FIRESTORE_PORT = 8080;
    // private IContainer? _storage;
    // private IContainer? _pubsub;
    // private IContainer? _firestore;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public static readonly string ProjectId = "orleans-test";
    // public static readonly string StorageEndpoint = $"http://localhost:{STORAGE_PORT}";
    public static readonly string PubSubEndpoint = $"http://localhost:{PUBSUB_PORT}";
    public static readonly string FirestoreEndpoint = $"http://localhost:{FIRESTORE_PORT}";

    private GoogleEmulatorHost() { }

    private static GoogleEmulatorHost? _instance;

    public static GoogleEmulatorHost Instance => _instance ??= new GoogleEmulatorHost();

    public async Task EnsureStarted()
    {
        await this._semaphore.WaitAsync();

        try
        {
            // if (this._storage is not null && this._pubsub is not null && this._firestore is not null) return;

            // var tasks = new List<Task>();

            // this._storage = new ContainerBuilder()
            //     .WithImage("oittaa/gcp-storage-emulator:latest")
            //     .WithPortBinding(STORAGE_PORT, STORAGE_PORT)
            //     .WithEnvironment("PORT", STORAGE_PORT.ToString())
            //     .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(STORAGE_PORT))
            //     .Build();
            // tasks.Add(this._storage.StartAsync());

            // this._firestore = new ContainerBuilder()
            //     .WithImage("gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators")
            //     .WithPortBinding(FIRESTORE_PORT, FIRESTORE_PORT)
            //     .WithCommand("gcloud", "emulators", "firestore", "start", $"--host-port=0.0.0.0:{FIRESTORE_PORT}")
            //     .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(FIRESTORE_PORT))
            //     .Build();
            // tasks.Add(this._firestore.StartAsync());

            // this._pubsub = new ContainerBuilder()
            //     .WithImage("gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators")
            //     .WithPortBinding(PUBSUB_PORT, PUBSUB_PORT)
            //     .WithCommand("gcloud", "beta", "emulators", "pubsub", "start", $"--host-port=0.0.0.0:{PUBSUB_PORT}")
            //     .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(PUBSUB_PORT))
            //     .Build();
            // tasks.Add(this._pubsub.StartAsync());

            // await Task.WhenAll(tasks);

            Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "http://127.0.0.1:8085");
            Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", "http://127.0.0.1:8080");
            // Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", StorageEndpoint);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Unable to start Google Cloud Platform emulators.", ex);
            throw;
        }
        finally
        {
            this._semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var tasks = new List<Task>();

        // if (this._storage is not null)
        // {
        //     tasks.Add(this._storage.StopAsync());
        // }

        // if (this._pubsub is not null)
        // {
        //     tasks.Add(this._pubsub.StopAsync());
        // }

        // if (this._firestore is not null)
        // {
        //     tasks.Add(this._firestore.StopAsync());
        // }

        await Task.WhenAll(tasks);
    }
}