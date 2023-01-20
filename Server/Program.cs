using System.Net;
using eShop.Data;
using eShop.Data.Base;
using eShop.Data.Repositories;
using eShop.Domain.Base;
using eShop.Domain.ShoppingCart.Entity;
using eShop.Domain.ShoppingCart.Requests;
using eShop.Domain.Stock;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orleans.Configuration;
using Orleans.Streaming.RabbitMQ.Configurators;
using RabbitMQ.Stream.Client;
using Server;

const string connectionString =
    "Server=localhost;Database=OrleansApp;User Id=iuri;Password=iuri;TrustServerCertificate=True;Max Pool Size=32000;";

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
        services.AddDbContext<ApplicationContext>(e =>
        {
            e.UseSqlServer(connectionString, a => a.EnableRetryOnFailure(6));
        });
        services.AddMediatR(typeof(GetCartRequest).Assembly);
        services.AddScoped<IShoppingCartRepository, ShoppingCartRepository>();
        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<IOffsetRepository, OffsetRepository>();
        services.AddScoped<IUnitOfWork>(s => s.GetRequiredService<ApplicationContext>());
        services.AddScoped<IDomainEventsProvider, DomainEventsProvider>();
    }).UseOrleans((context, siloBuilder) =>
    {
        var systemDataSqlclient = "System.Data.SqlClient";

        siloBuilder.UseAdoNetClustering(options =>
            {
                options.Invariant = systemDataSqlclient;
                options.ConnectionString = connectionString;
            }).Configure<EndpointOptions>(options =>
            {
                // Port to use for silo-to-silo
                //options.SiloPort = 11_111;
                //// Port to use for the gateway
                //options.GatewayPort = 30_000;
                // IP Address to advertise in the cluster
                options.AdvertisedIPAddress = IPAddress.Parse("192.168.0.41");
                // The socket used for client-to-silo will bind to this endpoint
                //options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 40_000);
                //// The socket used by the gateway will bind to this endpoint
                //options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 50_000);
            })
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "orleans-rabbitmq";
                options.ServiceId = "orleans-rabbitmq";
            })
            //.UseDashboard(e => { })
            .AddAdoNetGrainStorage("ListenerGrainStore", options =>
            {
                options.Invariant = systemDataSqlclient;
                options.ConnectionString = connectionString;

            })
            .AddMemoryGrainStorage("PubSubStore")
            .AddRabbitMQStreams("RabbitMQ", configuration => {

                configuration.ConfigureRabbitMQ(a => a.Configure(e =>
                {
                    var lbAddressResolver = new AddressResolver(new IPEndPoint(IPAddress.Parse("192.168.0.19"), 5552));
                    e.StreamSystemConfig = new StreamSystemConfig
                    {
                        VirtualHost = "/",
                        UserName = "iuri",
                        Password = "iuri",
                        AddressResolver = lbAddressResolver,
                        Endpoints = new List<EndPoint> { lbAddressResolver.EndPoint }
                    };
                    e.IntervalToUpdateOffset = TimeSpan.FromSeconds(20);
                }));
                configuration.ConfigurePartitioning(8);
                //configuration.ConfigureCache(100);
                //configuration.ConfigureCache(1);
            })
            .Configure<GrainCollectionOptions>(options =>
            {
                options.ActivationTimeout = TimeSpan.FromHours(2);
                options.CollectionAge = TimeSpan.FromSeconds(2);
                options.CollectionQuantum = TimeSpan.FromSeconds(1);
            });
    })
    .Build();

host.Run();




