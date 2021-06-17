using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// The silo builder startup extensions.
    /// </summary>
    public static class SiloBuilderStartupExtensions
    {
        /// <summary>
        /// Adds a startup task to be executed when the silo has started.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="stage">
        /// The stage to execute the startup task, see values in <see cref="ServiceLifecycleStage"/>.
        /// </param>
        /// <typeparam name="TStartup">
        /// The startup task type.
        /// </typeparam>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>.
        /// </returns>
        public static ISiloBuilder AddStartupTask<TStartup>(
            this ISiloBuilder builder,
            int stage = ServiceLifecycleStage.Active)
            where TStartup : class, IStartupTask
        {
            return builder.AddStartupTask((sp, ct) => ActivatorUtilities.GetServiceOrCreateInstance<TStartup>(sp).Execute(ct), stage);
        }

        /// <summary>
        /// Adds a startup task to be executed when the silo has started.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="startupTask">
        /// The startup task.
        /// </param>
        /// <param name="stage">
        /// The stage to execute the startup task, see values in <see cref="ServiceLifecycleStage"/>.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>.
        /// </returns>
        public static ISiloBuilder AddStartupTask(
            this ISiloBuilder builder,
            IStartupTask startupTask,
            int stage = ServiceLifecycleStage.Active)
        {
            return builder.AddStartupTask((sp, ct) => startupTask.Execute(ct), stage);
        }

        /// <summary>
        /// Adds a startup task to be executed when the silo has started.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="startupTask">
        /// The startup task.
        /// </param>
        /// <param name="stage">
        /// The stage to execute the startup task, see values in <see cref="ServiceLifecycleStage"/>.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>.
        /// </returns>
        public static ISiloBuilder AddStartupTask(
            this ISiloBuilder builder,
            Func<IServiceProvider, CancellationToken, Task> startupTask,
            int stage = ServiceLifecycleStage.Active)
        {
            builder.ConfigureServices(services =>
                services.AddTransient<ILifecycleParticipant<ISiloLifecycle>>(sp =>
                    new StartupTask(
                        sp,
                        startupTask,
                        stage)));
            return builder;
        }

        /// <inheritdoc />
        private class StartupTask : ILifecycleParticipant<ISiloLifecycle>
        {
            private readonly IServiceProvider serviceProvider;
            private readonly Func<IServiceProvider, CancellationToken, Task> startupTask;

            private readonly int stage;

            public StartupTask(
                IServiceProvider serviceProvider,
                Func<IServiceProvider, CancellationToken, Task> startupTask,
                int stage)
            {
                this.serviceProvider = serviceProvider;
                this.startupTask = startupTask;
                this.stage = stage;
            }

            /// <inheritdoc />
            public void Participate(ISiloLifecycle lifecycle)
            {
                lifecycle.Subscribe<StartupTask>(
                    this.stage,
                    cancellation => this.startupTask(this.serviceProvider, cancellation));
            }
        }
    }
}