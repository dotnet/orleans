using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using System.Collections.Generic;
using UnitTests.Grains;
using Xunit;

namespace NonSilo.Tests.Reminders
{
    [TestCategory("BVT")]
    [TestCategory("Reminders")]
    public class ReminderServiceConfigurationTests
    {
        /// <summary>
        /// Tests that if an IRemindable grain is registered, then an IReminderTable must also be registered.
        /// </summary>
        [Fact]
        public void ReminderService_ConfigurationValidatorTest()
        {
            var exception = Assert.Throws<OrleansConfigurationException>(() => new SiloHostBuilder()
                .UseLocalhostClustering()
                .ConfigureApplicationParts(parts => parts.AddFeatureProvider(new RemindableGrainFeatureProvider()))
                .Build());

            Assert.Contains(nameof(ReminderTestGrain2), exception.Message);

            var builder = new SiloHostBuilder()
                .UseLocalhostClustering()
                .ConfigureApplicationParts(parts => parts.AddFeatureProvider(new RemindableGrainFeatureProvider()))
                .UseInMemoryReminderService();
            using (var silo = builder.Build())
            {
                Assert.NotNull(silo);
            }
        }

        private class RemindableGrainFeatureProvider : IApplicationFeatureProvider<GrainClassFeature>
        {
            public void PopulateFeature(IEnumerable<IApplicationPart> parts, GrainClassFeature feature)
            {
                feature.Classes.Add(new GrainClassMetadata(typeof(ReminderTestGrain2)));
            }
        }
    }
}
