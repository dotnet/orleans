using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Startup;

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
        /// <typeparam name="TStartup">
        /// The startup task type.
        /// </typeparam>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder AddStartupTask<TStartup>(this ISiloHostBuilder builder)
            where TStartup : class, IStartupTask
        {
            EnsureStartupAdded(builder);
            builder.ConfigureServices(services => services.AddSingleton<IStartupTask, TStartup>());
            return builder;
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
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder AddStartupTask(this ISiloHostBuilder builder, IStartupTask startupTask)
        {
            EnsureStartupAdded(builder);
            builder.ConfigureServices(services => services.AddSingleton(startupTask));
            return builder;
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
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>.
        /// </returns>
        public static ISiloHostBuilder AddStartupTask(this ISiloHostBuilder builder, Func<IServiceProvider, CancellationToken, Task> startupTask)
        {
            EnsureStartupAdded(builder);
            builder.ConfigureServices(services => services.AddSingleton<IStartupTask>(sp => new DelegateStartupTask(sp, startupTask)));
            return builder;
        }

        private static void EnsureStartupAdded(ISiloHostBuilder builder)
        {
            if (!builder.Properties.TryGetValue(typeof(StartupTask), out var _))
            {
                builder.ConfigureServices(
                    services =>
                    {
                        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, StartupTask>();
                        services.TryAddSingleton<StartupTaskSystemTarget>();
                    });
                builder.Properties[typeof(StartupTask)] = true;
            }
        }

        private class StartupTask : ILifecycleParticipant<ISiloLifecycle>
        {
            private readonly IEnumerable<IStartupTask> startupTasks;

            private readonly OrleansTaskScheduler scheduler;

            private readonly SiloProviderRuntime siloProviderRuntime;

            private readonly StartupTaskSystemTarget schedulingTarget;

            public StartupTask(
                IEnumerable<IStartupTask> startupTasks,
                OrleansTaskScheduler scheduler,
                StartupTaskSystemTarget schedulingTarget,
                SiloProviderRuntime siloProviderRuntime)
            {
                this.startupTasks = startupTasks;
                this.scheduler = scheduler;
                this.siloProviderRuntime = siloProviderRuntime;
                this.schedulingTarget = schedulingTarget;
            }

            public void Participate(ISiloLifecycle lifecycle)
            {
                lifecycle.Subscribe(SiloLifecycleStage.RuntimeServices, this.RegisterSystemTarget);
                lifecycle.Subscribe(SiloLifecycleStage.SiloActive, this.OnStarted);
            }

            private Task RegisterSystemTarget(CancellationToken cancellationToken)
            {
                // Register the target used for scheduling startup tasks.
                this.siloProviderRuntime.RegisterSystemTarget(this.schedulingTarget);
                return Task.CompletedTask;
            }

            private Task OnStarted(CancellationToken cancellationToken)
            {
                return this.scheduler.QueueTask(
                    async () =>
                    {
                        foreach (var task in this.startupTasks)
                        {
                            await task.Execute(cancellationToken);
                        }
                    },
                    this.schedulingTarget.SchedulingContext);
            }
        }

        // A dummy system target for scheduling startup tasks.
        internal class StartupTaskSystemTarget : SystemTarget
        {
            public StartupTaskSystemTarget(ILocalSiloDetails localSiloDetails, ILoggerFactory loggerFactory)
                : base(Constants.StartupTaskSystemTargetId, localSiloDetails.SiloAddress, loggerFactory)
            {
            }
        }

        private class DelegateStartupTask : IStartupTask
        {
            private readonly IServiceProvider serviceProvider;

            private readonly Func<IServiceProvider, CancellationToken, Task> startupTask;

            public DelegateStartupTask(IServiceProvider serviceProvider, Func<IServiceProvider, CancellationToken, Task> startupTask)
            {
                this.serviceProvider = serviceProvider;
                this.startupTask = startupTask;
            }

            public Task Execute(CancellationToken cancellationToken) => this.startupTask(this.serviceProvider, cancellationToken);
        }
    }
}