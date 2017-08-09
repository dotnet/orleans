using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Tester
{
    [TestCategory("BVT"), TestCategory("OrleansLogging")]
    public class OrleansLoggingTests
    {
        [Fact]
        public void CanCreateLoggerFromDI()
        {
            //configure default logging
            IServiceCollection serviceCollection = new ServiceCollection();
            Silo.ConfigureDefaultLogging(serviceCollection, $"{this.GetType().Name}.log", new IPEndPoint(102187443, 11113));
            var serviceProvider = serviceCollection.BuildServiceProvider();

            //get logger
            var logger = serviceProvider.GetService<ILogger<OrleansLoggingTests>>();
            Assert.NotNull(logger);
            logger.Log<string>(LogLevel.Information, OrleansLoggingDecorator.CreateEventId(0, 0), "Successfully logged one message", null, (msg, exc) => msg);
            //supports orleans legacy log method
            logger.Info(0,"Successfully logged one message", null);

            //dispose log providers
            this.DisposeLogProviders(serviceProvider);
        }

        [Fact]
        public void OrleansLoggingCanConfigurePerCategoryServeriyOverrides()
        {
            //configure logging with severity overrides
            IServiceCollection serviceCollection = new ServiceCollection();
            var loggerProvider = new OrleansLoggerProvider()
                .AddLogConsumer(new FileLogConsumer($"{this.GetType().Name}.log", new IPEndPoint(102187443, 11113)))
                .AddSeverityOverrides(this.GetType().FullName, Severity.Warning);
            serviceCollection.AddLogging(builder => builder.AddProvider(loggerProvider));
            var serviceProvider = serviceCollection.BuildServiceProvider();
            //get logger
            var logger = serviceProvider.GetRequiredService<ILogger<OrleansLoggingTests>>();
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(logger.IsEnabled(LogLevel.Information));

            //dispose log providers
            this.DisposeLogProviders(serviceProvider);
        }

        [Fact]
        public void MicrosoftExtensionsLogging_LoggingFilter_CanAlsoConfigurePerCategoryLogLevel()
        {
            //configure logging with severity overrides
            IServiceCollection serviceCollection = new ServiceCollection();
            var loggerProvider = new OrleansLoggerProvider()
                .AddLogConsumer(new FileLogConsumer($"{this.GetType().Name}.log", new IPEndPoint(102187443, 11113)));
            serviceCollection.AddLogging(builder =>
             builder.AddProvider(loggerProvider)
             .AddFilter(this.GetType().FullName, LogLevel.Warning)
             );
            var serviceProvider = serviceCollection.BuildServiceProvider();
            //get logger
            var logger = serviceProvider.GetRequiredService<ILogger<OrleansLoggingTests>>();
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(logger.IsEnabled(LogLevel.Information));

            //dispose log providers
            this.DisposeLogProviders(serviceProvider);
        }

        private void DisposeLogProviders(IServiceProvider svc)
        {
            var providers = svc.GetServices<ILoggerProvider>();
            foreach (var provider in providers)
            {
                provider.Dispose();
            }
        }
    }
}
