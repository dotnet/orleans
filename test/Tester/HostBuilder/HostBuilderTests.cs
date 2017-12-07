using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Tester.HostBuilder.Fakes;
using Xunit;

namespace Tester.HostBuilder
{
    [TestCategory("BVT")]
    public class HostBuilderTests
    {
        [Fact]
        public void ConfigureHostConfigurationPropagated()
        {
            var host = CreateBuilder()
                .ConfigureHostConfiguration(configBuilder =>
                {
                    configBuilder.AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string>("key1", "value1")
                    });
                })
                .ConfigureHostConfiguration(configBuilder =>
                {
                    configBuilder.AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string>("key2", "value2")
                    });
                })
                .ConfigureHostConfiguration(configBuilder =>
                {
                    configBuilder.AddInMemoryCollection(new[]
                    {
                        // Hides value2
                        new KeyValuePair<string, string>("key2", "value3")
                    });
                })
                .ConfigureAppConfiguration((context, configBuilder) =>
                {
                    Assert.Equal("value1", context.Configuration["key1"]);
                    Assert.Equal("value3", context.Configuration["key2"]);
                    var config = configBuilder.Build();
                    Assert.Equal("value1", config["key1"]);
                    Assert.Equal("value3", config["key2"]);
                })
                .Build();

            using (host)
            {
                var config = host.Services.GetRequiredService<IConfiguration>();
                Assert.Equal("value1", config["key1"]);
                Assert.Equal("value3", config["key2"]);
            }
        }

        [Fact]
        public void CanConfigureAppConfigurationAndRetrieveFromDI()
        {
            var hostBuilder = CreateBuilder()
                .ConfigureAppConfiguration((configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(
                            new KeyValuePair<string, string>[]
                            {
                                new KeyValuePair<string, string>("key1", "value1")
                            });
                })
                .ConfigureAppConfiguration((configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(
                            new KeyValuePair<string, string>[]
                            {
                                new KeyValuePair<string, string>("key2", "value2")
                            });
                })
                .ConfigureAppConfiguration((configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(
                            new KeyValuePair<string, string>[]
                            {
                                // Hides value2
                                new KeyValuePair<string, string>("key2", "value3")
                            });
                });

            using (var host = hostBuilder.Build())
            {
                var config = host.Services.GetService<IConfiguration>();
                Assert.NotNull(config);
                Assert.Equal("value1", config["key1"]);
                Assert.Equal("value3", config["key2"]);
            }
        }

        [Fact, TestCategory("BVT")]
        public void DefaultIHostingEnvironmentValues()
        {
            var hostBuilder = CreateBuilder()
                .ConfigureAppConfiguration((hostContext, appConfig) =>
                {
                    var env = hostContext.HostingEnvironment;
                    Assert.Equal(EnvironmentName.Production, env.EnvironmentName);
                    Assert.Null(env.ApplicationName);
                });

            using (var host = hostBuilder.Build())
            {
                var env = host.Services.GetRequiredService<IHostingEnvironment>();
                Assert.Equal(EnvironmentName.Production, env.EnvironmentName);
                Assert.Null(env.ApplicationName);
            }
        }

        [Fact]
        public void ConfigBasedSettingsConfigBasedOverride()
        {
            var settings = new Dictionary<string, string>
            {
                { HostDefaults.EnvironmentKey, "EnvA" }
            };

            var overrideSettings = new Dictionary<string, string>
            {
                { HostDefaults.EnvironmentKey, "EnvB" }
            };

            var hostBuilder = CreateBuilder()
                .ConfigureHostConfiguration(configBuilder => configBuilder.AddInMemoryCollection(settings))
                .ConfigureHostConfiguration(configBuilder => configBuilder.AddInMemoryCollection(overrideSettings));

            using (var host = hostBuilder.Build())
            {
                Assert.Equal("EnvB", host.Services.GetRequiredService<IHostingEnvironment>().EnvironmentName);
            }
        }

        [Fact]
        public void UseEnvironmentIsNotOverriden()
        {
            var vals = new Dictionary<string, string>
            {
                { "ENV", "Dev" },
            };

            var expected = "MY_TEST_ENVIRONMENT";

            using (var host = CreateBuilder()
                .ConfigureHostConfiguration(configBuilder => configBuilder.AddInMemoryCollection(vals))
                .UseEnvironment(expected)
                .Build())
            {
                Assert.Equal(expected, host.Services.GetService<IHostingEnvironment>().EnvironmentName);
            }
        }

        [Fact]
        public void BuildAndDispose()
        {
            using (var host = CreateBuilder()
                .Build()) { }
        }

        [Fact]
        public void HostConfigParametersReadCorrectly()
        {
            var parameters = new Dictionary<string, string>()
            {
                { "applicationName", "MyProjectReference"},
                { "environment", EnvironmentName.Development},
            };

            var host = CreateBuilder()
                .ConfigureHostConfiguration(config =>
                {
                    config.AddInMemoryCollection(parameters);
                }).Build();

            var env = host.Services.GetRequiredService<IHostingEnvironment>();

            Assert.Equal("MyProjectReference", env.ApplicationName);
            Assert.Equal(EnvironmentName.Development, env.EnvironmentName);
        }


        [Fact]
        public void DefaultServicesAreAvailable()
        {
            using (var host = CreateBuilder()
                .Build())
            {
                Assert.NotNull(host.Services.GetRequiredService<IHostingEnvironment>());
                Assert.NotNull(host.Services.GetRequiredService<IConfiguration>());
                Assert.NotNull(host.Services.GetRequiredService<HostBuilderContext>());
                // Assert.NotNull(host.Services.GetRequiredService<IApplicationLifetime>());
                Assert.NotNull(host.Services.GetRequiredService<ILoggerFactory>());
                Assert.NotNull(host.Services.GetRequiredService<IOptions<FakeOptions>>());
            }
        }

        [Fact]
        public void DefaultCreatesLoggerFactory()
        {
            var hostBuilder = CreateBuilder();

            using (var host = hostBuilder.Build())
            {
                Assert.NotNull(host.Services.GetService<ILoggerFactory>());
            }
        }


        [Fact]
        public void MultipleConfigureLoggingInvokedInOrder()
        {
            var callCount = 0; //Verify ordering
            var hostBuilder = CreateBuilder()
                .ConfigureLogging((hostContext, loggerFactory) =>
                {
                    Assert.Equal(0, callCount++);
                })
                .ConfigureLogging((hostContext, loggerFactory) =>
                {
                    Assert.Equal(1, callCount++);
                });

            using (hostBuilder.Build())
            {
                Assert.Equal(2, callCount);
            }
        }

        [Fact]
        public void HostingContextContainsAppConfigurationDuringConfigureServices()
        {
            var hostBuilder = CreateBuilder()
                 .ConfigureAppConfiguration((configBuilder) =>
                    configBuilder.AddInMemoryCollection(
                        new KeyValuePair<string, string>[]
                        {
                            new KeyValuePair<string, string>("key1", "value1")
                        }))
                 .ConfigureServices((context, factory) =>
                 {
                     Assert.Equal("value1", context.Configuration["key1"]);
                 });

            using (hostBuilder.Build()) { }
        }

        [Fact]
        public void ConfigureDefaultServiceProvider()
        {
            var hostBuilder = CreateBuilder()
                .ConfigureServices((s) =>
                {
                    s.AddTransient<ServiceD>();
                    s.AddScoped<ServiceC>();
                })
                .UseServiceProviderFactory(new DefaultServiceProviderFactory(new ServiceProviderOptions()
                {
                    ValidateScopes = true,
                }));
            var host = hostBuilder.Build();

            Assert.Throws<InvalidOperationException>(() => { host.Services.GetRequiredService<ServiceC>(); });
        }

        [Fact]
        public void ConfigureCustomServiceProvider()
        {
            var hostBuilder = CreateBuilder()
                .ConfigureServices((hostContext, s) =>
                {
                    s.AddTransient<ServiceD>();
                    s.AddScoped<ServiceC>();
                })
                .UseServiceProviderFactory(new FakeServiceProviderFactory())
                .ConfigureContainer<FakeServiceCollection>((context, container) =>
                {
                    Assert.Null(container.State);
                    container.State = "1";
                })
                .ConfigureContainer<FakeServiceCollection>((context, container) =>
                {
                    Assert.Equal("1", container.State);
                    container.State = "2";
                });
            var host = hostBuilder.Build();
            var fakeServices = host.Services.GetRequiredService<FakeServiceCollection>();
            Assert.Equal("2", fakeServices.State);
        }

        [Fact]
        public void CustomContainerTypeMismatchThrows()
        {
            var hostBuilder = CreateBuilder()
                .ConfigureServices((s) =>
                {
                    s.AddTransient<ServiceD>();
                    s.AddScoped<ServiceC>();
                })
                .UseServiceProviderFactory(new FakeServiceProviderFactory());

            Assert.Throws<InvalidCastException>(() => 
                hostBuilder.ConfigureContainer<IServiceCollection>((context, container) => { }));

            // In the generic host, the exception is thrown here instead:
            // Assert.Throws<InvalidCastException>(() => hostBuilder.Build());
        }

        [Fact]
        public void HostingContextContainsAppConfigurationDuringConfigureLogging()
        {
            var hostBuilder = CreateBuilder()
                 .ConfigureAppConfiguration((configBuilder) =>
                    configBuilder.AddInMemoryCollection(
                        new KeyValuePair<string, string>[]
                        {
                            new KeyValuePair<string, string>("key1", "value1")
                        }))
                 .ConfigureLogging((context, factory) =>
                 {
                     Assert.Equal("value1", context.Configuration["key1"]);
                 });

            using (hostBuilder.Build()) { }
        }

        [Fact]
        public void ConfigureServices_CanBeCalledMultipleTimes()
        {
            var callCount = 0; // Verify ordering
            var hostBuilder = CreateBuilder()
                .ConfigureServices((services) =>
                {
                    Assert.Equal(0, callCount++);
                    services.AddTransient<ServiceA>();
                })
                .ConfigureServices((services) =>
                {
                    Assert.Equal(1, callCount++);
                    services.AddTransient<ServiceB>();
                });

            using (var host = hostBuilder.Build())
            {
                Assert.Equal(2, callCount);

                Assert.NotNull(host.Services.GetRequiredService<ServiceA>());
                Assert.NotNull(host.Services.GetRequiredService<ServiceB>());
            }
        }

        [Fact]
        public void Build_DoesNotAllowBuildingMuiltipleTimes()
        {
            var builder = CreateBuilder();
            using (builder.Build())
            {
                var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
                Assert.StartsWith("Build can only be called once", ex.Message);
            }
        }

        [Fact]
        public void BuilderPropertiesAreAvailableInBuilderAndContext()
        {
            var hostBuilder = CreateBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    Assert.Equal("value", hostContext.Properties["key"]);
                });

            hostBuilder.Properties.Add("key", "value");

            Assert.Equal("value", hostBuilder.Properties["key"]);

            using (hostBuilder.Build()) { }
        }

        private static SiloHostBuilder CreateBuilder()
        {
            var builder = new SiloHostBuilder();
            builder.WithFakeHost();
            return builder;
        }

        private class ServiceC
        {
            public ServiceC(ServiceD serviceD) { }
        }

        internal class ServiceD { }

        internal class ServiceA { }

        internal class ServiceB { }
    }
}
