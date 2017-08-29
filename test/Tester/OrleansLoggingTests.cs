﻿using Microsoft.Extensions.DependencyInjection;
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
            serviceCollection.AddLogging();
            var serviceProvider = serviceCollection.BuildServiceProvider();

            //get logger
            var logger = serviceProvider.GetService<ILogger<OrleansLoggingTests>>();
            Assert.NotNull(logger);
            logger.LogInformation("Successfully logged one message");
            //supports orleans legacy log method
            logger.LogInformation("Successfully logged");

            //dispose log providers
            this.DisposeLogProviders(serviceProvider);
        }

        [Fact]
        [Obsolete]
        public void OrleansLoggingCanConfigurePerCategoryServeriyOverrides()
        {
            //configure logging with severity overrides
            IServiceCollection serviceCollection = new ServiceCollection();
            var loggerProvider = new OrleansLoggerProvider()
                .AddLogConsumer(new FileLogConsumer($"{this.GetType().Name}.log", new IPEndPoint(102187443, 11113)))
                .AddSeverityOverrides(this.GetType().FullName, Severity.Warning);
            var loggerFac = new LoggerFactory();
            loggerFac.AddProvider(loggerProvider);
            serviceCollection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            serviceCollection.AddSingleton<ILoggerFactory>(loggerFac);
            //swtich to serviceCollection.AddLogging(builder => builder.AddProvider(loggerProvider)) after upgrade to Microsoft.Extensions.Logging 2.0
            // logBuilder is not supported in 1.1.3
            var serviceProvider = serviceCollection.BuildServiceProvider();
            //get logger
            var logger = serviceProvider.GetRequiredService<ILogger<OrleansLoggingTests>>();
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(logger.IsEnabled(LogLevel.Information));

            //dispose log providers
            this.DisposeLogProviders(serviceProvider);
        }
        /*
        [Fact]
        TODO: enable this after upgrade to Microsoft.Extensions.Logging 2.0. LogFilter or LogBuilder isn't supported in 1.1.2
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
        }*/

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
