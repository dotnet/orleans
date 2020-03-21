using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneBoxDeployment.Api.StartupTask
{
    /// <summary>
    /// A market interface to asynchronous startup jobs.
    /// </summary>
    /// <remarks>Based on code by Andrew Lock at https://andrewlock.net/running-async-tasks-on-app-startup-in-asp-net-core-part-4-using-health-checks/.
    /// See some problems and improvements
    /// <ul>
    ///     <li>https://tools.ietf.org/html/draft-inadarei-api-health-check-02</li>
    ///     <li>https://github.com/aspnet/AspNetCore/issues/5936</li>
    /// </ul>
    /// </remarks>
    public interface IStartupTask: IHostedService { }


    /// <summary>
    /// Starts and connects Orleans cluster client asynchronously.
    /// </summary>
    public class ClusterClientStartupTask: BackgroundService, IStartupTask
    {
        /// <summary>
        /// The shared context does not let the pipeline to accept calls
        /// before all asynchrous start-up jobs have completed.
        /// </summary>
        private StartupTaskContext StartupContext { get; }

        /// <summary>
        /// The cluster client to connect to Orleans cluster asynchronously.
        /// </summary>
        private IClusterClient ClusterClient { get; }

       /// <summary>
       /// The logger to log operations.
       /// </summary>
        private ILogger Logger { get; }


        /// <summary>
        /// The default constructorl.
        /// </summary>
        /// <param name="startupTaskContext">The asynchronous context.</param>
        /// <param name="clusterClient">The cluster client to connect to Orleans cluster asynchronously.</param>
        /// <param name="logger">The logger to use to log operations.</param>
        public ClusterClientStartupTask(StartupTaskContext startupTaskContext, IClusterClient clusterClient, ILogger<ClusterClientStartupTask> logger)
        {
            StartupContext = startupTaskContext ?? throw new ArgumentNullException(nameof(startupTaskContext));
            ClusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Runs the cluster client startup task.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            await ClusterClient.Connect(async ex =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

            StartupContext.MarkTaskAsComplete();
        }
    }
}
