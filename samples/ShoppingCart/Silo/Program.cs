// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

await Host.CreateDefaultBuilder(args)
    .UseOrleans(
        (context, builder) =>
        {
            if (context.HostingEnvironment.IsDevelopment())
            {
                builder.UseLocalhostClustering()
                    .AddMemoryGrainStorage("shopping-cart")
                    .AddStartupTask<SeedProductStoreTask>();
            }
            else
            {
                var endpointAddress =
                    IPAddress.Parse(context.Configuration["WEBSITE_PRIVATE_IP"]);
                var strPorts =
                    context.Configuration["WEBSITE_PRIVATE_PORTS"].Split(',');
                if (strPorts.Length < 2)
                    throw new Exception("Insufficient private ports configured.");
                var (siloPort, gatewayPort) =
                    (int.Parse(strPorts[0]), int.Parse(strPorts[1]));
                var connectionString =
                    context.Configuration["ORLEANS_AZURE_STORAGE_CONNECTION_STRING"];

                builder
                    .ConfigureEndpoints(endpointAddress, siloPort, gatewayPort)
                    .Configure<ClusterOptions>(
                        options =>
                        {
                            options.ClusterId = "ShoppingCartCluster";
                            options.ServiceId = nameof(ShoppingCartService);
                        }).UseAzureStorageClustering(                    
                    options => options.ConfigureTableServiceClient(connectionString));
                builder.AddAzureTableGrainStorage(
                    "shopping-cart",                    
                    options => options.ConfigureTableServiceClient(connectionString));
            }
        })
    .ConfigureWebHostDefaults(
        webBuilder => webBuilder.UseStartup<Startup>())
    .RunConsoleAsync();