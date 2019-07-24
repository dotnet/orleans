using System;
using System.Threading.Tasks;
using HelloWorld.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;

namespace TestClient
{
    /// <summary>
    /// A simple wrapper around IClusterClient to demonstrate the C++ interop
    /// </summary>
    public class OrleansClientWrapper
    {
        /// <summary>
        /// This assembly is build as exe so the Main() is required.
        /// However, it shouldn't be called directly and the method is here just to make the compiler happy.
        /// </summary>
        public static void Main() => throw new InvalidOperationException("Not meant to run directly as a console app");

        private static IClusterClient _client;
        private static ILogger _logger;

        /// <summary>
        /// Callback used to tell the C++ caller that the client is initialized.
        /// </summary>
        /// <param name="success">Whether the client was initialized or not</param>
        public delegate void InitializeCallback(bool success);

        /// <summary>
        /// Callback used to tell the C++ the result of a grain invocation.
        /// </summary>
        /// <param name="message">Message to be sent back</param>
        public delegate void HelloCallback(string message);

        static OrleansClientWrapper()
        {
            _client = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureLogging(builder => builder.AddConsole())
                .ConfigureApplicationParts(app => app.AddApplicationPart(typeof(IHello).Assembly))
                .Build();
            _logger = _client.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                    .CreateLogger<OrleansClientWrapper>();
        }

        public static void Initialize(InitializeCallback callback)
        {
            InitializeClient().ContinueWith(_ =>
            {
                callback(_client.IsInitialized);
            }).ConfigureAwait(false);
        }

        public static void SayHello(HelloCallback callback)
        {
            var friend = _client.GetGrain<IHello>(0);
            friend.SayHello("Good morning, my friend!").ContinueWith(async task =>
            {
                var msg = await task;
                _logger.LogInformation("\n\n{0} - {1}\n\n", msg, "C#");
                callback(msg);
            }).ConfigureAwait(false);
        }

        private static Task InitializeClient()
        {
            var attempt = 0;
            var maxAttempts = 100;
            var delay = TimeSpan.FromSeconds(1);
            return _client.Connect(async error =>
            {
                if (++attempt < maxAttempts)
                {
                    _logger.LogWarning(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    try
                    {
                        await Task.Delay(delay);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    return true;
                }
                else
                {
                    _logger.LogError(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    return false;
                }
            });
        }
    }
}
