using Client;
using Orleans.Configuration;


const string connectionString =
    "Server=192.168.0.41,1433;Database=OrleansApp;User Id=iuri;Password=iuri;TrustServerCertificate=True;";

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
    })
    .UseOrleansClient(conifg =>
    {
        
        conifg.Configure<ClusterOptions>(opt =>
        {
            opt.ClusterId = "orleans-rabbitmq";
            opt.ServiceId = "orleans-rabbitmq";
        }).Configure<ClientMessagingOptions>(opt =>
            {
                opt.DropExpiredMessages = true;
                opt.ResponseTimeout = TimeSpan.FromHours(1);
                opt.ResponseTimeoutWithDebugger = TimeSpan.FromHours(1);
            })
            .UseAdoNetClustering(config =>
        {
            config.ConnectionString = connectionString;
            config.Invariant = "System.Data.SqlClient";
        });
    })
    .Build();

host.Run();
