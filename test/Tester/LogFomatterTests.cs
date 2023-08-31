using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;

namespace Tester
{
    [TestCategory("BVT")]
    public class LogFomatterTests
    {
        [Fact]
        public void CanResolveFormatter()
        {
            // expected output
            TestLoggerFactory expected = BuildOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
            // ensure logging output is as expected
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void CanResolveGenericFormatter()
        {
            // expected output
            TestLoggerFactory expected = BuildOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
            // ensure logging output is as expected
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void GenericFormatterRedact()
        {
            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.Configure<TestOptionsWithSecrets>(options => {
                options.Data = "Hello";
                options.Password = "v3ryS3cur3!!!";
                options.SomeConnectionString = "DefaultEndpointsProtocol=https;AccountName=someAccount;AccountKey=someKey;EndpointSuffix=core.windows.net";
            });
            services.ConfigureFormatter<TestOptionsWithSecrets>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptionsWithSecrets>);
            // ensure logging output is as expected
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Contains("Hello", actual.ToString());
            Assert.Contains("Password: REDACTED", actual.ToString());
            Assert.Contains("SomeConnectionString: DefaultEndpointsProtocol=https;AccountName=someAccount;AccountKey=<--SNIP-->", actual.ToString());
        }

        [Fact]
        public void GenericFormatterWithListAndDictionary()
        {
            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.Configure<TestOptionsWithListAndDictionary>(options => {
                options.SomeDictionary.Add("Account1", "Key1");
                options.SomeDictionary.Add("Account2", "Key2");
                options.SomeDictionary.Add("Account3", "Key3");
                options.SomeList.Add(10);
                options.SomeList.Add(11);
            });
            services.ConfigureFormatter<TestOptionsWithListAndDictionary>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptionsWithListAndDictionary>);
            // ensure logging output is as expected
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            var txt = actual.ToString();
            Assert.Contains("SomeList.0: 10", actual.ToString());
            Assert.Contains("SomeList.1: 11", actual.ToString());
            Assert.Contains("SomeDictionary.Account1: Key1", actual.ToString());
            Assert.Contains("SomeDictionary.Account2: Key2", actual.ToString());
            Assert.Contains("SomeDictionary.Account3: Key3", actual.ToString());
            Assert.Contains("NullList:", actual.ToString());
            Assert.Contains("NullDictionary:", actual.ToString());
        }

        [Fact]
        public void FormatterConfiguredTwiceDoesNotLeadToDuplicatedFormatter()
        {
            // expected output
            TestLoggerFactory expected = BuildOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter2>();
            //the formatter configured second time will override the first one in DI and
            //DI will end up with two formatter for the same option
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            // only one options formater and it points to the right one
            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
            // when resolving singe type specific formatter, we get the right one
            var logFormatter = servicesProvider.GetService<IOptionFormatter<TestOptions>>();
            Assert.True(logFormatter is TestOptionsFormatter);
            // ensure logging output is as expected
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void CustomFormatterOverridesDefaultFormatter_PreRegistration()
        {
            // expected output
            TestLoggerFactory expected = BuildOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.Configure<TestOptions>(options => options.IntField = 1);
            // pre register overrides
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter>();
            // default
            services.TryConfigureFormatter<TestOptions, TestOptionsFormatter2>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
            // ensure logging output is as expected
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void CustomFormatterOverridesDefaultFormatter_PostRegistration()
        {
            // expected output
            TestLoggerFactory expected = BuildOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.Configure<TestOptions>(options => options.IntField = 1);
            // default
            services.TryConfigureFormatter<TestOptions, TestOptionsFormatter2>();
            // post register overrides
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
            // when resolving singe type specific formatter, we get the right one
            var logFormatter = servicesProvider.GetService<IOptionFormatter<TestOptions>>();
            Assert.True(logFormatter is TestOptionsFormatter);
            // ensure logging output is as expected
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void NamedFormatterGoldenPath()
        {
            // expected output
            TestLoggerFactory expected = BuildNamedOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            services.ConfigureFormatterResolver<TestOptions, TestOptionsFormatter.Resolver>();
            Enumerable
                .Range(1, 3)
                .ToList()
                .ForEach(i =>
                {
                    string name = i.ToString();
                    services.Configure<TestOptions>(name, (options => options.IntField = i));
                    services.ConfigureNamedOptionForLogging<TestOptions>(name);
                });
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Equal(3, logFormatters.Count());
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.ElementAt(1) is TestOptionsFormatter);
            Assert.True(logFormatters.ElementAt(2) is TestOptionsFormatter);
            var logFormatter = servicesProvider.GetService<IOptionFormatterResolver<TestOptions>>();
            Assert.True(logFormatter is TestOptionsFormatter.Resolver);
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void NamedGenericFormatterGoldenPath()
        {
            // expected output
            TestLoggerFactory expected = BuildNamedOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.AddSingleton(typeof(IOptionFormatterResolver<>), typeof(DefaultOptionsFormatterResolver<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            Enumerable
                .Range(1, 3)
                .ToList()
                .ForEach(i =>
                {
                    string name = i.ToString();
                    services.Configure<TestOptions>(name, (options => options.IntField = i));
                    services.ConfigureNamedOptionForLogging<TestOptions>(name);
                });
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Equal(3, logFormatters.Count());
            var logFormatter = servicesProvider.GetService<IOptionFormatterResolver<TestOptions>>();
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void CustomFormatterResolverOverridesDefaultFormatter_PreRegistration()
        {
            // expected output
            TestLoggerFactory expected = BuildNamedOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            // pre register overrides
            services.ConfigureFormatterResolver<TestOptions, TestOptionsFormatter.Resolver>();
            // configure options
            Enumerable
                .Range(1, 3)
                .ToList()
                .ForEach(i =>
                {
                    string name = i.ToString();
                    services.Configure<TestOptions>(name, (options => options.IntField = i));
                    services.ConfigureNamedOptionForLogging<TestOptions>(name);
                });
            // default
            services.TryConfigureFormatterResolver<TestOptions, TestOptionsFormatter2.Resolver>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Equal(3, logFormatters.Count());
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.ElementAt(1) is TestOptionsFormatter);
            Assert.True(logFormatters.ElementAt(2) is TestOptionsFormatter);
            var logFormatter = servicesProvider.GetService<IOptionFormatterResolver<TestOptions>>();
            Assert.True(logFormatter is TestOptionsFormatter.Resolver);
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Fact]
        public void CustomFormatterResolverOverridesDefaultFormatter_PostRegistration()
        {
            // expected output
            TestLoggerFactory expected = BuildNamedOptionsExpectedResult();

            // actual output
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<TestLoggerFactory>();
            services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<TestLoggerFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            services.AddSingleton<OptionsLogger, TestOptionsLogger>();
            // defaults
            services.TryConfigureFormatterResolver<TestOptions, TestOptionsFormatter2.Resolver>();
            // configure options
            Enumerable
                .Range(1, 3)
                .ToList()
                .ForEach(i =>
                {
                    string name = i.ToString();
                    services.Configure<TestOptions>(name, (options => options.IntField = i));
                    services.ConfigureNamedOptionForLogging<TestOptions>(name);
                });
            // post register overrides
            services.ConfigureFormatterResolver<TestOptions, TestOptionsFormatter.Resolver>();
            var servicesProvider = services.BuildServiceProvider();
            servicesProvider.GetRequiredService<OptionsLogger>().LogOptions();

            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Equal(3, logFormatters.Count());
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.ElementAt(1) is TestOptionsFormatter);
            Assert.True(logFormatters.ElementAt(2) is TestOptionsFormatter);
            var logFormatter = servicesProvider.GetService<IOptionFormatterResolver<TestOptions>>();
            Assert.True(logFormatter is TestOptionsFormatter.Resolver);
            var actual = servicesProvider.GetRequiredService<TestLoggerFactory>();
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        private TestLoggerFactory BuildOptionsExpectedResult()
        {
            var services = new ServiceCollection();
            var testOptions = new TestOptions
            {
                IntField = 1
            };
            var expected = new TestLoggerFactory();
            var formatter = new TestOptionsFormatter(Options.Create(testOptions));
            var optionsLogger = new TestOptionsLogger(expected.CreateLogger<TestOptionsLogger>(), services.BuildServiceProvider());
            optionsLogger.LogOption(formatter);
            return expected;
        }

        private TestLoggerFactory BuildNamedOptionsExpectedResult()
        {
            var services = new ServiceCollection();
            IOptionFormatter[] formatters = Enumerable
                .Range(1, 3)
                .Select(i =>  TestOptionsFormatter.CreateNamed(i.ToString(), Options.Create(new TestOptions { IntField = i })))
                .ToArray<IOptionFormatter>();
            var expected = new TestLoggerFactory();
            var optionsLogger = new TestOptionsLogger(expected.CreateLogger<TestOptionsLogger>(), services.BuildServiceProvider());
            optionsLogger.LogOptions(formatters);
            return expected;
        }

        private class TestOptionsWithSecrets
        {
            [Redact()]
            public string Password { get; set; }

            public string Data { get; set; }

            [RedactConnectionString()]
            public string SomeConnectionString { get; set; }
        }

        private class TestOptions
        {
            public int IntField { get; set; } = 0;
        }

        private class TestOptionsWithListAndDictionary
        {
            public List<int> SomeList { get; set; } = new List<int>();

            public Dictionary<string, string> SomeDictionary { get; set; } = new Dictionary<string, string>();

            public List<int> NullList { get; set; } = null;

            public Dictionary<string, string> NullDictionary { get; set; } = null;
        }

        private class TestOptionsFormatter2 : IOptionFormatter<TestOptions>
        {
            public string Name { get; private set; }

            private readonly TestOptions options;
            public TestOptionsFormatter2(IOptions<TestOptions> options)
            {
                this.options = options.Value;
                this.Name = nameof(TestOptions);
            }

            public static TestOptionsFormatter2 CreateNamed(string name, IOptions<TestOptions> options)
            {
                var result = new TestOptionsFormatter2(options);
                // different format
                result.Name = $"{nameof(TestOptions)}+{name}";
                return result;
            }

            public IEnumerable<string> Format()
            {
                return new List<string>()
                {
                    // different format
                    OptionFormattingUtilities.Format(nameof(options.IntField), options.IntField, "{0}=>{1}")
                };
            }

            public class Resolver : IOptionFormatterResolver<TestOptions>
            {
                private readonly IOptionsMonitor<TestOptions> optionsMonitor;
                public Resolver(IOptionsMonitor<TestOptions> optionsMonitor)
                {
                    this.optionsMonitor = optionsMonitor;
                }

                public IOptionFormatter<TestOptions> Resolve(string name)
                {
                    return TestOptionsFormatter2.CreateNamed(name, Options.Create(this.optionsMonitor.Get(name)));
                }
            }
        }

        private class TestOptionsFormatter : IOptionFormatter<TestOptions>
        {
            public string Name { get; private set; }

            private readonly TestOptions options;
            public TestOptionsFormatter(IOptions<TestOptions> options)
            {
                this.options = options.Value;
                this.Name = typeof(TestOptions).FullName;
            }

            public static TestOptionsFormatter CreateNamed(string name, IOptions<TestOptions> options)
            {
                var result = new TestOptionsFormatter(options);
                result.Name = $"{typeof(TestOptions).FullName}-{name}";
                return result;
            }

            public IEnumerable<string> Format()
            {
                return new List<string>()
                {
                    OptionFormattingUtilities.Format(nameof(options.IntField), options.IntField)
                };
            }

            public class Resolver : IOptionFormatterResolver<TestOptions>
            {
                private readonly IOptionsMonitor<TestOptions> optionsMonitor;
                public Resolver(IOptionsMonitor<TestOptions> optionsMonitor)
                {
                    this.optionsMonitor = optionsMonitor;
                }

                public IOptionFormatter<TestOptions> Resolve(string name)
                {
                    return TestOptionsFormatter.CreateNamed(name, Options.Create(this.optionsMonitor.Get(name)));
                }
            }
        }

        private class TestLoggerFactory : ILoggerFactory
        {
            private readonly ConcurrentDictionary<string, Logger> loggers = new ConcurrentDictionary<string, Logger>();

            public void AddProvider(ILoggerProvider provider)
            {
                throw new NotImplementedException();
            }

            public ILogger CreateLogger(string categoryName)
            {
                return this.loggers.GetOrAdd(categoryName, new Logger());
            }

            public void Dispose()
            {
            }

            public override string ToString()
            {
                return string.Join(":", this.loggers.Select(kvp => $"{kvp.Key} =\n{kvp.Value.ToString()}\n"));
            }

            private class Logger : ILogger
            {
                private readonly List<string> entries = new List<string>();

                public IDisposable BeginScope<TState>(TState state)
                {
                    throw new NotImplementedException();
                }

                public override string ToString()
                {
                    return string.Join(";", this.entries);
                }

                public bool IsEnabled(LogLevel logLevel)
                {
                    return true;
                }

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    entries.Add(formatter(state, exception));
                }
            }
        }

        private class TestLogger<T> : ILogger<T>
        {
            private readonly ILogger<T> logger;

            public TestLogger(ILoggerFactory loggerFactory)
            {
                this.logger = loggerFactory.CreateLogger<T>();
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return logger.BeginScope<TState>(state);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logger.IsEnabled(logLevel);
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                logger.Log<TState>(logLevel, eventId, state, exception, formatter);
            }
        }


        private class TestOptionsLogger : OptionsLogger
        {
            public TestOptionsLogger(ILogger<TestOptionsLogger> logger, IServiceProvider services)
                : base(logger, services)
            {
            }
        }
    }
}
