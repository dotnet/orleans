using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin()
    .WithEndpoint(port: 5552, targetPort: 5552, name: "stream")
    .WithBindMount("enabled_plugins", "/etc/rabbitmq/enabled_plugins", isReadOnly: true)
    .WithEnvironment(
        "RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS",
        "-rabbitmq_stream advertised_host 127.0.0.1 advertised_port 5552");

var streamEndpoint = rabbitmq.GetEndpoint("stream");

builder.AddProject<RabbitMQ_Silo>("silo")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq)
    .WithEnvironment("RABBITMQ_STREAM_ADDRESS", streamEndpoint.Property(EndpointProperty.Host))
    .WithEnvironment("RABBITMQ_STREAM_PORT", streamEndpoint.Property(EndpointProperty.Port))
    .WithEnvironment("RABBITMQ_STREAM_USER", rabbitmq.Resource.UserNameReference)
    .WithEnvironment("RABBITMQ_STREAM_PASSWORD", rabbitmq.Resource.PasswordParameter);

builder.Build().Run();
