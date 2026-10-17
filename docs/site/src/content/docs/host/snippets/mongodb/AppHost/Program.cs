using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// <mongodb_aspire_apphost>
var database = builder.AddMongoDB("mongo").AddDatabase("orders");
var orleans = builder.AddOrleans("orders-cluster")
    .WithClustering(database)
    .WithGrainStorage("profiles", database)
    .WithReminders(database);

builder.AddProject<Projects.MongoDB>("silo")
    .WithReference(orleans)
    .WaitFor(database);
// </mongodb_aspire_apphost>

await builder.Build().RunAsync();
