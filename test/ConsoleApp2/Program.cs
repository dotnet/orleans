using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Error);
    })
    .UseOrleans(siloBuilder =>
    {
        siloBuilder.UseLocalhostClustering();
    });

var host = builder.Build();

var siloLifetime = host.Services.GetRequiredService<ISiloLifetime>();

Console.WriteLine("Registering lifecycle hooks");

try
{
    siloLifetime.Started.Register(async _ => await Task.Delay(100));
    Console.WriteLine("Started.Register should have thrown exception!");
}
catch (NotSupportedException)
{
    Console.WriteLine("Started.Register correctly threw NotSupportedException.");
}

_ = Task.Run(async () =>
{
    await siloLifetime.Started.Task;
    Console.WriteLine("Silo 'Started' task completed.");
});

_ = Task.Run(async () =>
{
    using var callback = siloLifetime.Stopping.Register(async _ =>
    {
        Console.WriteLine("Callback should have not executed, it was disposed!");
    });

    Console.WriteLine("Doing temporary operation like uploading some file.");

    await Task.Delay(100);

    Console.WriteLine("Finished temporary operation! Dont care about the callback anymore.");
});

_ = Task.Run(async () =>
{
    Console.WriteLine("Background worker started. Running until 'Stopping' token triggers.");
    var token = siloLifetime.Stopping.CancellationToken;

    while (!token.IsCancellationRequested)
    {
        await Task.Delay(500);
    }

    Console.WriteLine("Background worker detected 'Stopping' signal and exited.");
});

siloLifetime.Stopping.Register(async ct =>
{
    Console.WriteLine("Silo 'Stopping' triggered. Executing 2-second cleanup.");
    try
    {
        await Task.Delay(2000, ct);
        Console.WriteLine("Silo 'Stopping' cleanup finished.");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cleanup was cancelled prematurely!");
    }
});

siloLifetime.Stopped.Register(async ct =>
{
    Console.WriteLine("Silo 'Stopped' triggered.");
    await Task.Delay(100, ct);
});

Console.WriteLine("Starting Host");

await host.StartAsync();

Console.WriteLine("Silo is running. Press a key to stop.");
Console.ReadKey();
Console.WriteLine("Stopping Host");

await host.StopAsync();

Console.WriteLine("Host has fully stopped. Testing late registration now.");

var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
siloLifetime.Stopping.Register(async _ =>
{
    Console.WriteLine("Late registered 'Stopping' callback executed.");
    tcs.SetResult();

    await Task.CompletedTask;
});

var finished = await Task.WhenAny(tcs.Task, Task.Delay(100)); // Need to leave some room for the BG task for late registrations to fire!
if (finished != tcs.Task)
{
    Console.WriteLine("Late callback did not execute (this should not have happened)");
}

Console.WriteLine("Application Exited. Press a key to exit.");
Console.ReadKey();
