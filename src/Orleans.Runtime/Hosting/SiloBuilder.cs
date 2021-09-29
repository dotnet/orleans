using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    /// <summary>
    /// Internal wrapper type of <see cref="IHostBuilder"/> that scope all configuration extensions related to orleans.
    /// </summary>
    internal class SiloBuilder : ISiloBuilder
    {
        private readonly IHostBuilder _hostBuilder;
        private readonly List<Action<HostBuilderContext, IServiceCollection>> _configureDelegates = new();
        private HostBuilderContext _context;
        private IServiceCollection _services;

        public SiloBuilder(IHostBuilder hostBuilder)
        {
            _hostBuilder = hostBuilder;
            this.ConfigureDefaults();
            hostBuilder.ConfigureServices((ctx, services) => InvokeConfigureServicesDelegates(ctx, services));
        }

        private void InvokeConfigureServicesDelegates(HostBuilderContext ctx, IServiceCollection services)
        {
            // Prevent future calls to ConfigureServices from enqueuing more delegates
            _context = ctx;
            _services = services;

            // Invoke all enqueued delegates
            foreach (var configureDelegate in _configureDelegates)
            {
                configureDelegate(ctx, services);
            }
        }

        /// <inheritdoc />
        public IDictionary<object, object> Properties => _hostBuilder.Properties;

        /// <inheritdoc />
        public ISiloBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));

            if (_services is { } services)
            {
                configureDelegate(_context, services);
            }
            else
            {
                _configureDelegates.Add(configureDelegate);
            }

            return this;
        }
    }
}