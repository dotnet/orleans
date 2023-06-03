using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Google.Tests;

public class GoogleEmulatorNotAvailableException : Exception
{
    public GoogleEmulatorNotAvailableException(string message) : base(message) { }
}

/// <summary>
/// Manages the lifecycle of the Google Cloud Platform emulators.
/// </summary>
public class GoogleEmulatorHost : IAsyncDisposable
{
    public const string GOOGLE_PROJECT_ID = "orleans-test";
    private const int STORAGE_PORT = 9594;
    private const int PUBSUB_PORT = 9596;
    private const int FIRESTORE_PORT = 9595;
    private readonly IContainer _storage;
    private readonly IContainer _pubsub;
    private readonly IContainer _firestore;

    public string StorageEndpoint => this._storage.State != TestcontainersStates.Running
                ? throw new GoogleEmulatorNotAvailableException("Google Cloud Storage emulator is not running.")
                : $"http://{this._storage.Hostname}:{this._storage.GetMappedPublicPort(STORAGE_PORT)}";

    public string PubSubEndpoint => this._pubsub.State != TestcontainersStates.Running
                ? throw new GoogleEmulatorNotAvailableException("Google Cloud PubSub emulator is not running.")
                : $"http://{this._pubsub.Hostname}:{this._pubsub.GetMappedPublicPort(PUBSUB_PORT)}";

    public string FirestoreEndpoint => this._firestore.State != TestcontainersStates.Running
                ? throw new GoogleEmulatorNotAvailableException("Google Cloud Firestore emulator is not running.")
                : $"http://{this._firestore.Hostname}:{this._firestore.GetMappedPublicPort(FIRESTORE_PORT)}";

    public GoogleEmulatorHost()
    {
        this._storage = new ContainerBuilder()
            .WithAutoRemove(false)
            .WithImage("oittaa/gcp-storage-emulator:latest")
            .WithPortBinding(STORAGE_PORT, true)
            .WithEnvironment("PORT", STORAGE_PORT.ToString())
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(STORAGE_PORT))
            .Build();

        this._pubsub = new ContainerBuilder()
            .WithImage("gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators")
            .WithPortBinding(PUBSUB_PORT, true)
            .WithCommand("gcloud", "beta", "emulators", "pubsub", "start", $"--host-port=0.0.0.0:{PUBSUB_PORT}", $"--project={GOOGLE_PROJECT_ID}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(PUBSUB_PORT))
            .Build();

        this._firestore = new ContainerBuilder()
            .WithImage("gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators")
            .WithPortBinding(FIRESTORE_PORT, true)
            .WithCommand("gcloud", "emulators", "firestore", "start", $"--host-port=0.0.0.0:{FIRESTORE_PORT}", $"--project={GOOGLE_PROJECT_ID}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(FIRESTORE_PORT))
            .Build();
    }

    public async Task Initialize()
    {
        await Task.WhenAll(
            this._storage.StartAsync(),
            this._pubsub.StartAsync(),
            this._firestore.StartAsync());

        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", this.FirestoreEndpoint);
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", this.PubSubEndpoint);
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", this.StorageEndpoint);
    }
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            this._storage.StopAsync(),
            this._pubsub.StopAsync(),
            this._firestore.StopAsync());
    }
}