IHostBuilder builder = Host.CreateDefaultBuilder(args)
    .UseOrleans(silo =>
    {
        silo.UseConsulSiloClustering(options =>
        {
            // The address of the Consul server
            var address = new Uri("http://localhost:8500");
            options.ConfigureConsulClient(address);
        });
    })
    .UseConsoleLifetime();

using IHost host = builder.Build();
host.Run();
