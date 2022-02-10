using AccountTransfer.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;

using var client = new ClientBuilder()
    .UseLocalhostClustering()
    .ConfigureLogging(logging => logging.AddConsole())
    .Build();

await client.Connect();

var accountNames = new[] { "Xaawo", "Pasqualino", "Derick", "Ida", "Stacy", "Xiao" };
var random = Random.Shared;

while (!Console.KeyAvailable)
{
    var atm = client.GetGrain<IAtmGrain>(0);

    // Choose some random accounts to exchange money
    var fromId = random.Next(accountNames.Length);
    var toId = random.Next(accountNames.Length);
    while (toId == fromId)
    {
        // Avoid transfering to/from the same account, since it would be meaningless
        toId = (toId + 1) % accountNames.Length;
    }

    var fromName = accountNames[fromId];
    var toName = accountNames[toId];
    var from = client.GetGrain<IAccountGrain>(fromName);
    var to = client.GetGrain<IAccountGrain>(toName);

    // Perform the transfer and query the results
    try
    {
        await atm.Transfer(from, to, 100);

        var fromBalance = await from.GetBalance();
        var toBalance = await to.GetBalance();

        Console.WriteLine(
            $"We transfered 100 credits from {fromName} to " +
            $"{toName}.\n{fromName} balance: {fromBalance}\n{toName} balance: {toBalance}\n");
    }
    catch (Exception exception)
    {
        Console.WriteLine(
            $"Error transfering 100 credits from "+
            $"{fromName} to {toName}: {exception.Message}");

        if (exception.InnerException is { } inner)
        {
            Console.WriteLine($"\tInnerException: {inner.Message}\n");
        }

        Console.WriteLine();
    }

    // Sleep and run again
    await Task.Delay(TimeSpan.FromMilliseconds(200));
}