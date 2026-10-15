using Azure.Storage.Blobs;
using Orleans.Journaling;

// Hosts a Garnet journal partition until the parent process kills it or closes standard input.
// Usage: Orleans.Journaling.Garnet.CrashHost <data-directory> <port>
//        Orleans.Journaling.Garnet.CrashHost blob <connection-string> <container> <storage-location> <epoch> <port>
GarnetJournalPartitionSettings settings;
int port;
if (args.Length == 2 && int.TryParse(args[1], out port))
{
    settings = new GarnetJournalPartitionSettings { DataDirectory = args[0], ListenEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), LogMemorySize = "64m", IndexMemorySize = "1m" };
}
else if (args.Length == 6 && args[0] == "blob" && long.TryParse(args[4], out var epoch) && int.TryParse(args[5], out port))
{
    var container = new BlobContainerClient(args[1], args[2]);
    var storage = await GarnetPartitionBlobStorage.OpenAsync(container, args[3], epoch, logger: null, CancellationToken.None);
    settings = new GarnetJournalPartitionSettings { BlobStorage = storage, ListenEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), LogMemorySize = "64m", IndexMemorySize = "1m" };
}
else
{
    Console.Error.WriteLine("Usage: Orleans.Journaling.Garnet.CrashHost <data-directory> <port> | blob <connection-string> <container> <storage-location> <epoch> <port>");
    return 2;
}

using var host = GarnetJournalPartitionHost.Start(settings, loggerFactory: null);
Console.WriteLine($"READY {host.EndPoint.Port}");
Console.Out.Flush();
Console.In.ReadToEnd();
return 0;
