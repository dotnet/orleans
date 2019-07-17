using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Client.Hosting
{
    public class OrleansHostedConection
    {
        IClusterClient client;
        ILogger<OrleansHostedConection> logger;

        public OrleansHostedConection(
            ILogger<OrleansHostedConection> logger)
        {
            this.logger = logger;
        }

        public async Task DisconnectAsync()
        {
            try
            {
                await this.client.Close();
            }
            catch (OrleansException error)
            {
                logger.LogWarning(error, "Error while gracefully disconnecting from Orleans cluster. Will ignore and continue to shutdown.");
            }
        }

        public async Task<IClusterClient> ConnectAsync(IClusterClient client, CancellationToken cancellationToken)
        {
            this.client = client;
            var attempt = 0;
            var maxAttempts = 100;
            var delay = TimeSpan.FromSeconds(1);
             await this.client.Connect(async error =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (++attempt < maxAttempts)
                {
                    logger.LogWarning(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    return true;
                }
                else
                {
                    logger.LogError(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    return false;
                }
            });
            return client;
        }
    }
}
