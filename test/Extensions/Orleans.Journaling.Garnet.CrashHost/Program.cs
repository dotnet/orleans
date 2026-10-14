using Orleans.Journaling;

// Hosts a Garnet journal partition until the parent process kills it or closes standard input.
// Usage: Orleans.Journaling.Garnet.CrashHost <data-directory> <port>
if (args.Length != 2 || !int.TryParse(args[1], out var port))
{
    Console.Error.WriteLine("Usage: Orleans.Journaling.Garnet.CrashHost <data-directory> <port>");
    return 2;
}

using var host = GarnetJournalPartitionHost.Start(
    new GarnetJournalPartitionSettings { DataDirectory = args[0], ListenEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), LogMemorySize = "64m", IndexMemorySize = "1m" },
    loggerFactory: null);
Console.WriteLine($"READY {host.EndPoint.Port}");
Console.Out.Flush();
Console.In.ReadToEnd();
return 0;
