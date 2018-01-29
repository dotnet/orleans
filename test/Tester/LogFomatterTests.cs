using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Xunit;

namespace Tester
{
    [TestCategory("BVT")]
    public class LogFomatterTests
    {
        [Fact]
        public void CanResolveFormatter()
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter>();
            var servicesProvider = services.BuildServiceProvider();
            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
        }

        [Fact]
        public void FormatterConfiguredTwiceLeadsToDuplicatedFormatter()
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter>();
            //the formatter configured second time will override the first one in DI and
            //DI will end up with two formatter for the same option
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter2>();
            var servicesProvider = services.BuildServiceProvider();
            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.True(logFormatters.Count() == 2);
            Assert.True(logFormatters.First() is TestOptionsFormatter2);
            Assert.True(logFormatters.ElementAt(1) is TestOptionsFormatter2);
        }

        [Fact]
        public void DefaultFormatterWontOverrideCustomFormatter()
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions, TestOptionsFormatter>();
            //TestOptionsFormatter2 is configured as the default 
            services.TryConfigureFormatter<TestOptions, TestOptionsFormatter2>();
            var servicesProvider = services.BuildServiceProvider();
            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
        }

        [Fact]
        public void NamedFormatterConfiguredTwiceLeadsToDuplicatedFormatter()
        {
            string name = "test";
            var services = new ServiceCollection();
            services.AddOptions();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions, TestOptionFormatterResolver>(name);
            //the formatter configured second time will override the first one in DI and
            //DI will end up with two formatter for the same option
            services.ConfigureFormatter<TestOptions, TestOptionFormatterResolver2>(name);
            var servicesProvider = services.BuildServiceProvider();
            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.True(logFormatters.Count() == 2);
            Assert.True(logFormatters.First() is TestOptionsFormatter2);
            Assert.True(logFormatters.ElementAt(1) is TestOptionsFormatter2);
        }

        [Fact]
        public void NamedDefaultFormatterWontOverrideCustomFormatter()
        {
            string name = "test";
            var services = new ServiceCollection();
            services.AddOptions();
            services.Configure<TestOptions>(options => options.IntField = 1);
            services.ConfigureFormatter<TestOptions, TestOptionFormatterResolver>(name);
            //TestOptionsFormatter2 is configured as the default 
            services.TryConfigureFormatter<TestOptions, TestOptionFormatterResolver2>(name);
            var servicesProvider = services.BuildServiceProvider();
            var logFormatters = servicesProvider.GetServices<IOptionFormatter>();
            Assert.Single(logFormatters);
            Assert.True(logFormatters.First() is TestOptionsFormatter);
            Assert.True(logFormatters.First() is IOptionFormatter<TestOptions>);
        }

        public class TestOptions
        {
            public int IntField { get; set; } = 1;
        }

        public class TestOptionsFormatter2 : IOptionFormatter<TestOptions>
        {
            public string Category { get; }

            public string Name => nameof(TestOptions);

            private TestOptions options;
            public TestOptionsFormatter2(IOptions<TestOptions> options)
            {
                this.options = options.Value;
            }

            public IEnumerable<string> Format()
            {
                return new List<string>()
                {
                    OptionFormattingUtilities.Format(nameof(options.IntField), options.IntField)
                };
            }
        }

        public class TestOptionsFormatter : IOptionFormatter<TestOptions>
        {
            public string Category { get; }

            public string Name => nameof(TestOptions);

            private TestOptions options;
            public TestOptionsFormatter(IOptions<TestOptions> options)
            {
                this.options = options.Value;
            }

            public IEnumerable<string> Format()
            {
               return new List<string>()
               {
                   OptionFormattingUtilities.Format(nameof(options.IntField), options.IntField)
               };
            }
        }

        public class TestOptionFormatterResolver : IOptionFormatterResolver<TestOptions>
        {
            private IOptionsSnapshot<TestOptions> optionsAccessor;
            public TestOptionFormatterResolver(IOptionsSnapshot<TestOptions> optionsAccessor)
            {
                this.optionsAccessor = optionsAccessor;
            }

            public IOptionFormatter<TestOptions> Resolve(string name)
            {
                return new TestOptionsFormatter(Options.Create(optionsAccessor.Get(name)));
            }
        }

        public class TestOptionFormatterResolver2 : IOptionFormatterResolver<TestOptions>
        {
            private IOptionsSnapshot<TestOptions> optionsAccessor;
            public TestOptionFormatterResolver2(IOptionsSnapshot<TestOptions> optionsAccessor)
            {
                this.optionsAccessor = optionsAccessor;
            }

            public IOptionFormatter<TestOptions> Resolve(string name)
            {
                return new TestOptionsFormatter2(Options.Create(optionsAccessor.Get(name)));
            }
        }
    }
}
