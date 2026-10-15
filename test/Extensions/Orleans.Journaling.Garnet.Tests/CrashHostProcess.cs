using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>A partition hosted in a child process.</summary>
internal sealed class CrashHostProcess : IDisposable
{
    private readonly Process _process;

    private CrashHostProcess(Process process, IPEndPoint endPoint)
    {
        _process = process;
        EndPoint = endPoint;
    }

    public IPEndPoint EndPoint { get; }

    public static Task<CrashHostProcess> StartAsync(string directory, CancellationToken cancellationToken)
        => StartAsync([directory], cancellationToken);

    /// <summary>Starts a partition on blob storage, taking it over at <paramref name="epoch"/>.</summary>
    public static Task<CrashHostProcess> StartOnBlobsAsync(string connectionString, string container, string storageLocation, long epoch, CancellationToken cancellationToken)
        => StartAsync(["blob", connectionString, container, storageLocation, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);

    private static async Task<CrashHostProcess> StartAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var port = GetFreePort();
        var hostPath = Path.Combine(AppContext.BaseDirectory, "Orleans.Journaling.Garnet.CrashHost.dll");
        var startInfo = new ProcessStartInfo(GetDotNetPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(hostPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(port.ToString());
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The crash host did not start.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    throw new InvalidOperationException($"The crash host exited: {await errors}");
                }

                if (line.StartsWith("READY ", StringComparison.Ordinal))
                {
                    return new(process, new IPEndPoint(IPAddress.Loopback, port));
                }
            }
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    private static string GetDotNetPath()
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path)
            ? path
            : "dotnet";

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
