using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Kills the process hosting a partition while appends are in flight, recovers the partition, and checks that every
/// acknowledged append survived.
/// </summary>
[TestSuite("SlowBVT")]
[TestProvider("None")]
[TestCategory("SlowBVT")]
public sealed class GarnetJournalDurabilityTests
{
    private const int JournalCount = 8;
    private const int Cycles = 4;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcknowledgedAppends_SurviveProcessKill()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        try
        {
            var ids = Enumerable.Range(0, JournalCount).Select(index => JournalId.Create("durability", index.ToString())).ToArray();
            var acknowledged = new int[JournalCount];
            var random = new Random(42);
            for (var cycle = 0; cycle <= Cycles; cycle++)
            {
                using var host = await CrashHostProcess.StartAsync(directory, CancellationToken);
                await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
                {
                    EndPoints = { host.EndPoint },
                    Protocol = RedisProtocol.Resp2,
                    AbortOnConnectFail = false,
                    AllowAdmin = true,
                    // SAVE checkpoints while writers append, which can exceed the default timeout on a loaded machine.
                    AsyncTimeout = (int)TimeSpan.FromSeconds(60).TotalMilliseconds,
                });
                await using var client = await GarnetTestRouting.CreateSinglePartitionRouterAsync(host.EndPoint, TimeSpan.FromSeconds(2), CancellationToken);

                // Every acknowledged append is present, in order, with no gaps. Appends whose replies were lost may
                // also be present; they become the baseline for the next cycle.
                for (var j = 0; j < JournalCount; j++)
                {
                    var values = await ReadValuesAsync(client, ids[j]);
                    Assert.True(values.Count >= acknowledged[j], $"Journal {j} lost acknowledged appends: {values.Count} < {acknowledged[j]} after cycle {cycle}.");
                    Assert.Equal(Enumerable.Range(0, values.Count), values);
                    acknowledged[j] = values.Count;
                }

                if (cycle == Cycles)
                {
                    break;
                }

                using var stop = new CancellationTokenSource();
                var baseline = acknowledged.ToArray();
                var writers = Enumerable.Range(0, JournalCount)
                    .Select(j => Task.Run(() => WriteAsync(client, ids[j], acknowledged, j, stop.Token)))
                    .ToArray();

                // Kill only after every writer has made progress in this cycle.
                var progressDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (Enumerable.Range(0, JournalCount).Any(j => Volatile.Read(ref acknowledged[j]) <= baseline[j]))
                {
                    Assert.True(DateTime.UtcNow < progressDeadline, $"Writers made no progress in cycle {cycle}: {string.Join(", ", acknowledged)}.");
                    if (Enumerable.Range(0, JournalCount).FirstOrDefault(j => writers[j].IsCompleted && Volatile.Read(ref acknowledged[j]) <= baseline[j], -1) is var stalled and >= 0)
                    {
                        Assert.Fail($"Writer {stalled} stopped without progress in cycle {cycle}.");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 + random.Next(300)), CancellationToken);
                if (cycle % 2 == 1)
                {
                    // Recover from a checkpoint plus the AOF written after it.
                    await connection.GetDatabase().ExecuteAsync("SAVE");
                    await Task.Delay(TimeSpan.FromMilliseconds(100 + random.Next(200)), CancellationToken);
                }

                host.Kill();
                stop.Cancel();
                await Task.WhenAll(writers);
                Assert.All(acknowledged, count => Assert.True(count > 0));
            }
        }
        finally
        {
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }
    }

    private static async Task WriteAsync(GarnetJournalRouter client, JournalId id, int[] acknowledged, int index, CancellationToken stop)
    {
        var storage = new GarnetJournalStorage(client, id, "json", compactionThresholdBytes: 0);
        try
        {
            await storage.GetMetadataAsync(CancellationToken);
            var next = Volatile.Read(ref acknowledged[index]);
            var buffer = new byte[4];
            while (!stop.IsCancellationRequested)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer, next);
                await storage.AppendAsync(new ReadOnlySequence<byte>(buffer), CancellationToken);
                Volatile.Write(ref acknowledged[index], ++next);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or RedisException)
        {
            // The host was killed; this append's outcome is unknown.
        }
    }

    private static async Task<List<int>> ReadValuesAsync(GarnetJournalRouter client, JournalId id)
    {
        var consumer = new CapturingConsumer();
        await new GarnetJournalStorage(client, id, "json", compactionThresholdBytes: 0).ReadAsync(consumer, CancellationToken);
        var bytes = consumer.Bytes.ToArray();
        Assert.Equal(0, bytes.Length % 4);
        var values = new List<int>(bytes.Length / 4);
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)));
        }

        return values;
    }

    /// <summary>A partition hosted in a child process.</summary>
    private sealed class CrashHostProcess : IDisposable
    {
        private readonly Process _process;

        private CrashHostProcess(Process process, IPEndPoint endPoint)
        {
            _process = process;
            EndPoint = endPoint;
        }

        public IPEndPoint EndPoint { get; }

        public static async Task<CrashHostProcess> StartAsync(string directory, CancellationToken cancellationToken)
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
            startInfo.ArgumentList.Add(directory);
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
}
