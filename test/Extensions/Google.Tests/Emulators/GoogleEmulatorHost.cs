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
                _storageEndpoint = $"http://localhost:{STORAGE_PORT}";
                Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", _storageEndpoint);
            }

            return _storageEndpoint;
        }
    }

    public static string? _pubSubEndpoint;
    public static string PubSubEndpoint
    {
        get
        {
            if (_pubSubEndpoint is null)
            {
                EnsureEmulator(PUBSUB_PORT);
                _pubSubEndpoint = $"http://localhost:{PUBSUB_PORT}";
                Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", _pubSubEndpoint);
            }

            return _pubSubEndpoint;
        }
    }

    public static string? _firestoreEndpoint;
    public static string FirestoreEndpoint
    {
        get
        {
            if (_firestoreEndpoint is null)
            {
                EnsureEmulator(FIRESTORE_PORT);
                _firestoreEndpoint = $"http://localhost:{FIRESTORE_PORT}";
                Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", _firestoreEndpoint);
            }

            return _firestoreEndpoint;
        }
    }

    private static void EnsureEmulator(int port)
    {
        using var client = new TcpClient();
        var result = client.BeginConnect("localhost", port, null, null);
        var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        if (!success)
        {
            throw new SkipException();
        }

        client.EndConnect(result);
    }
}