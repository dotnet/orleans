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
                builder.AddAzureTableGrainStorage(
                    "shopping-cart",
                    options =>
                    {
                        options.UseJson = true;
                        var serviceUri = new Uri(context.Configuration["ServiceUri"]);
                        options.ConfigureTableServiceClient(
                            serviceUri, new DefaultAzureCredential());
                    });
            }
        })
    .ConfigureWebHostDefaults(
        webBuilder => webBuilder.UseStartup<Startup>())
    .RunConsoleAsync();