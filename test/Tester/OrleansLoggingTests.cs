using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Orleans.Logging.Legacy;
using Xunit;

namespace Tester
{
    [TestCategory("BVT"), TestCategory("OrleansLogging")]
    public class OrleansLoggingTests
    {
        [Fact]
#pragma warning disable 618
        public void OrleansLoggingCanConfigurePerCategoryServeriyOverrides()
        {
            //configure logging with severity overrides
            IServiceCollection serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging();
            var severityOverrides = new OrleansLoggerSeverityOverrides();
            severityOverrides.LoggerSeverityOverrides.Add(this.GetType().FullName, Severity.Warning);
            serviceCollection.AddLogging(builder => builder.AddLegacyOrleansLogging(new List<ILogConsumer>()
            {
                new LegacyFileLogConsumer($"{this.GetType().Name}.log")
            }, severityOverrides));
            var serviceProvider = serviceCollection.BuildServiceProvider();
            //get logger
            var logger = serviceProvider.GetRequiredService<ILogger<OrleansLoggingTests>>();
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(logger.IsEnabled(LogLevel.Information));

            //dispose log providers
           (serviceProvider as IDisposable)?.Dispose();
        }
       
        [Fact]
#pragma warning disable 618
        public void MicrosoftExtensionsLogging_LoggingFilter_CanAlsoConfigurePerCategoryLogLevel()
        {
            //configure logging with severity overrides
            IServiceCollection serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging(builder => builder.AddLegacyOrleansLogging(new List<ILogConsumer>()
                 {
                     new LegacyFileLogConsumer($"{this.GetType().Name}.log")
                 })
             .AddFilter(this.GetType().FullName, LogLevel.Warning)
             );
            var serviceProvider = serviceCollection.BuildServiceProvider();
            //get logger
            var logger = serviceProvider.GetRequiredService<ILogger<OrleansLoggingTests>>();
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.False(logger.IsEnabled(LogLevel.Information));

            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }

        [Fact]
#pragma warning disable 618
        public async Task MicrosoftExtensionsLogging_Messagebulking_ShouldWork()
        {
            var statefulLogConsumer = new StatefulLogConsumer();
            var messageBulkingConfig = new EventBulkingOptions();
            messageBulkingConfig.BulkEventInterval = TimeSpan.FromSeconds(2);
            var serviceProvider = new ServiceCollection().AddLogging(builder => 
            builder.AddLegacyOrleansLogging(new List<ILogConsumer>(){statefulLogConsumer}, null, messageBulkingConfig))
            .BuildServiceProvider();
            var logger = serviceProvider.GetService<ILogger<OrleansLoggingTests>>();
            //the appearance of the same event
            var sameEventCount = messageBulkingConfig.BulkEventLimit + 5;
            var eventId = 5;
            var message = "Producing event 5";
            var count = 0;
            while (count++ < sameEventCount)
            {
                logger.LogInformation(eventId, message);
            }
            //same event message should only appear BulkMessageLimit times
            Assert.Equal(messageBulkingConfig.BulkEventLimit, statefulLogConsumer.ReceivedMessages.Where(m => m.Equals(message)).Count());
            await Task.Delay(TimeSpan.FromSeconds(3));
            logger.LogInformation(eventId, message);
            //after 3 seconds, the event cound summary message should be flushed to log consumers
            Assert.True(statefulLogConsumer.ReceivedMessages.Where(m => m.Contains("additional time(s) in previous")).Count() > 0);

            //dispose log providers
            (serviceProvider as IDisposable)?.Dispose();
        }


        public class StatefulLogConsumer : ILogConsumer
        {
            public IList<string> ReceivedMessages { get; private set; } = new List<string>();

            public void Log(Severity severity, LoggerType loggerType, string caller, string message, IPEndPoint ipEndPoint, Exception exception, int eventCode = 0)
            {
                this.ReceivedMessages.Add(message);
            }
        }
    }
}
