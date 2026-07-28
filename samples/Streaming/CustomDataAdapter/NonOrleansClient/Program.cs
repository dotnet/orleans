using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Common;

var secrets = Secrets.TryLoadFromFile();
if (secrets is null)
{
    Console.Error.WriteLine("ERROR: This sample requires Azure Event Hub configuration.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Please create a Secrets.json file with the following structure:");
    Console.Error.WriteLine("""
    {
        "DataConnectionString": "<Azure Storage connection string>",
        "EventHubConnectionString": "<Event Hub connection string>"
    }
    """);
    Console.Error.WriteLine();
    Console.Error.WriteLine("For local development without Azure, use the 'Simple' streaming sample instead,");
    Console.Error.WriteLine("which supports in-memory streaming.");
    return 1;
}

// Sending event to a stream
// Here the StreamGuid will be encoded as the PartitionKey, and the namespace as a property of the event

await using (var client = new EventHubProducerClient(secrets.EventHubConnectionString, Constants.EHPath))
{
    var key = Guid.NewGuid().ToString();
    var options = new SendEventOptions { PartitionKey = Guid.NewGuid().ToString() };

    Console.WriteLine($"Sending event to StreamId: [{key}, {Constants.StreamNamespace}]");

    for (var i = 0; i < 30; i++)
    {
        Console.WriteLine($"Sending '{i}'");

        var evt = new EventData(JsonSerializer.SerializeToUtf8Bytes(i));
        evt.Properties["StreamNamespace"] = Constants.StreamNamespace;

        await client.SendAsync(new[] { evt }, options);
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}
Console.WriteLine("Done!");
return 0;
