using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.TestKit
{
    [Trait("Category", "BVT")]
    [ExcludeFromCodeCoverage]
    public abstract class CopierTester<TValue, TCopier> where TCopier : class, IDeepCopier<TValue>
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CodecProvider _codecProvider;

        protected CopierTester(ITestOutputHelper output)
        {
#if NET6_0_OR_GREATER
            var seed = Random.Shared.Next();
#else
            var seed = new Random().Next();
#endif
            output.WriteLine($"Random seed: {seed}");
            Random = new(seed);
            var services = new ServiceCollection();
            _ = services.AddSerializer(builder => builder.Configure(config => config.Copiers.Add(typeof(TCopier))));

            if (!typeof(TCopier).IsAbstract && !typeof(TCopier).IsInterface)
            {
                _ = services.AddSingleton<TCopier>();
            }

            _ = services.AddSerializer(Configure);

            _serviceProvider = services.BuildServiceProvider();
            _codecProvider = _serviceProvider.GetRequiredService<CodecProvider>();
        }

        protected Random Random { get; }

        protected IServiceProvider ServiceProvider => _serviceProvider;

        protected virtual bool IsImmutable => false;

        protected virtual void Configure(ISerializerBuilder builder)
        {
        }

        protected virtual TCopier CreateCopier() => _serviceProvider.GetRequiredService<TCopier>();
        protected abstract TValue CreateValue();
        protected abstract TValue[] TestValues { get; }
        protected virtual bool Equals(TValue left, TValue right) => EqualityComparer<TValue>.Default.Equals(left, right);

        protected virtual Action<Action<TValue>> ValueProvider { get; }

        [Fact]
        public void CopiedValuesAreEqual()
        {
            var copier = CreateCopier();
            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var output = copier.DeepCopy(original, new CopyContext(_codecProvider, _ => { }));
                var isEqual = Equals(original, output);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Copy value \"{output}\" must equal original value \"{original}\"");
            }
        }

        [Fact]
        public void ReferencesAreAddedToCopyContext()
        {
            if (typeof(TValue).IsValueType)
            {
                return;
            }

            var value = CreateValue();
            var array = new TValue[] { value, value };
            var arrayCopier = _serviceProvider.GetRequiredService<DeepCopier<TValue[]>>();
            var arrayCopy = arrayCopier.Copy(array);
            Assert.Same(arrayCopy[0], arrayCopy[1]);

            if (IsImmutable)
            {
                Assert.Same(value, arrayCopy[0]);
            }
            else
            {
                Assert.NotSame(value, arrayCopy[0]);
            }
        }
    }
}