using AccountTransfer.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using IHost host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient(client =>
    {
        client.UseLocalhostClustering()
            .UseTransactions();
    })
    .UseConsoleLifetime()
    .Build();

await host.StartAsync();

var client = host.Services.GetRequiredService<IClusterClient>();
var transactionClient = host.Services.GetRequiredService<ITransactionClient>();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

var accountNames = new[] { "Xaawo", "Pasqualino", "Derick", "Ida", "Stacy", "Xiao" };
var random = Random.Shared;

while (!lifetime.ApplicationStopping.IsCancellationRequested)
{
    // Choose some random accounts to exchange money
    var fromIndex = random.Next(accountNames.Length);
    var toIndex = random.Next(accountNames.Length);
    while (toIndex == fromIndex)
    {
        // Avoid transferring to/from the same account, since it would be meaningless
        toIndex = (toIndex + 1) % accountNames.Length;
    }

    var fromKey = accountNames[fromIndex];
    var toKey = accountNames[toIndex];
    var fromAccount = client.GetGrain<IAccountGrain>(fromKey);
    var toAccount = client.GetGrain<IAccountGrain>(toKey);

    // Perform the transfer and query the results
    try
    {
        var transferAmount = random.Next(200);

        await transactionClient.RunTransaction(
            TransactionOption.Create,
            async () =>
            {
                await fromAccount.Withdraw(transferAmount);
                await toAccount.Deposit(transferAmount);
            });

        var fromBalance = await fromAccount.GetBalance();
        var toBalance = await toAccount.GetBalance();

        Console.WriteLine(
            $"We transferred {transferAmount} credits from {fromKey} to " +
            $"{toKey}.\n{fromKey} balance: {fromBalance}\n{toKey} balance: {toBalance}\n");
    }
    catch (Exception exception)
    {
        Console.WriteLine(
            $"Error transferring credits from " +
            $"{fromKey} to {toKey}: {exception.Message}");

        if (exception.InnerException is { } inner)
        {
            Console.WriteLine($"\tInnerException: {inner.Message}\n");
        }

        Console.WriteLine();
    }

    // Sleep and run again
    await Task.Delay(TimeSpan.FromMilliseconds(200));
}