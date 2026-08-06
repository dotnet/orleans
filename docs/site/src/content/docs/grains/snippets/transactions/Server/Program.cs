using Azure.Data.Tables;

await Host.CreateDefaultBuilder(args)
    .UseOrleans((_, silo) =>
    {
        silo.UseLocalhostClustering();

        if (Environment.GetEnvironmentVariable(
                "ORLEANS_STORAGE_CONNECTION_STRING") is { } connectionString)
        {
            silo.AddAzureTableTransactionalStateStorage(
                "TransactionStore", 
                options => options.TableServiceClient = new TableServiceClient(connectionString));
        }
        else
        {
            silo.AddMemoryGrainStorageAsDefault();
        }

        silo.UseTransactions();
    })
    .RunConsoleAsync();
