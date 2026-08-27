using System.Net;
using System.Net.Sockets;

namespace Orleans.Tests.Google;

public static class GoogleEmulatorHost
{
    private const int STORAGE_PORT = 9199;
    private const int PUBSUB_PORT = 8085;
    private const int FIRESTORE_PORT = 8080;

    public const string ProjectId = "orleans-test";

    private static string? _storageEndpoint;
    public static string StorageEndpoint
    {
        get
        {
            if (_storageEndpoint is null)
            {
                EnsureEmulator(STORAGE_PORT);
                _storageEndpoint = $"http://127.0.0.1:{STORAGE_PORT}";
                Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", _storageEndpoint);
            }

            return _storageEndpoint;
        }
    }

    private static string? _pubSubEndpoint;
    public static string PubSubEndpoint
    {
        get
        {
            if (_pubSubEndpoint is null)
            {
                EnsureEmulator(PUBSUB_PORT);
                _pubSubEndpoint = $"http://127.0.0.1:{PUBSUB_PORT}";
                Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", _pubSubEndpoint);
            }

            return _pubSubEndpoint;
        }
    }

    private static string? _firestoreEndpoint;
    public static string FirestoreEndpoint
    {
        get
        {
            if (_firestoreEndpoint is null)
            {
                EnsureEmulator(FIRESTORE_PORT);
                _firestoreEndpoint = $"http://127.0.0.1:{FIRESTORE_PORT}";
                Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", _firestoreEndpoint);
            }

            return _firestoreEndpoint;
        }
    }

    private static void EnsureEmulator(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.ConnectAsync(IPAddress.Loopback, port)
                .WaitAsync(TimeSpan.FromSeconds(1), Xunit.TestContext.Current.CancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("The Google emulator is not available");
        }
    }
}