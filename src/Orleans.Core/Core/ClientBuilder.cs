using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans
{
    /// <summary>
    /// Builder used for creating <see cref="IClusterClient"/> instances.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        private readonly IHostBuilder _hostBuilder;
        private readonly List<Action<HostBuilderContext, IServiceCollection>> _configureDelegates = new();
        private HostBuilderContext _context;
        private IServiceCollection _services;

        public ClientBuilder(IHostBuilder hostBuilder)
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
        public IClientBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
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