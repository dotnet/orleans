using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;
using System.Linq;

namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Test methods for copiers.
    /// </summary>
    [Trait("Category", "BVT")]
    [ExcludeFromCodeCoverage]
    public abstract class CopierTester<TValue, TCopier> where TCopier : class, IDeepCopier<TValue>
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CodecProvider _codecProvider;

        /// <summary>
        /// Initializes a new <see cref="CopierTester{TValue, TCopier}"/> instance.
        /// </summary>
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

        /// <summary>
        /// Gets the random number generator.
        /// </summary>
        protected Random Random { get; }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        protected IServiceProvider ServiceProvider => _serviceProvider;

        /// <summary>
        /// Gets a value indicating whether the type copied by this codec is immutable.
        /// </summary>
        protected virtual bool IsImmutable => false;

        /// <summary>
        /// Gets a value indicating whether the type copied by this codec is pooled.
        /// </summary>
        protected virtual bool IsPooled => false;

        /// <summary>
        /// Configures the serializer.
        /// </summary>
        protected virtual void Configure(ISerializerBuilder builder)
        {
        }

        /// <summary>
        /// Creates a copier instance for testing.
        /// </summary>
        protected virtual TCopier CreateCopier() => _serviceProvider.GetRequiredService<TCopier>();

        /// <summary>
        /// Creates a value to copy.
        /// </summary>
        protected abstract TValue CreateValue();

        /// <summary>
        /// Gets an array of test values.
        /// </summary>
        protected abstract TValue[] TestValues { get; }

        /// <summary>
        /// Compares two values and returns <see langword="true"/> if they are equal, or <see langword="false"/> if they are not equal.
        /// </summary>
        protected virtual bool Equals(TValue left, TValue right) => EqualityComparer<TValue>.Default.Equals(left, right);

        /// <summary>
        /// Gets a value provider delegate.
        /// </summary>
        protected virtual Action<Action<TValue>> ValueProvider { get; }

        /// <summary>
        /// Checks if copied values are equal.
        /// </summary>
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

        /// <summary>
        /// Checks if references are added to the copy context.
        /// </summary>
        [Fact]
        public void ReferencesAreAddedToCopyContext()
        {
            if (typeof(TValue).IsValueType || IsPooled)
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

        /// <summary>
        /// Checks if strongly-typed tuples containing the field type can be copied.
        /// </summary>
        [Fact]
        public void CanCopyTupleViaSerializer()
        {
            var copier = _serviceProvider.GetRequiredService<DeepCopier<(string, TValue, TValue, string)>>();

            var original = (Guid.NewGuid().ToString(), CreateValue(), CreateValue(), Guid.NewGuid().ToString());

            var copy = copier.Copy(original);

            var isEqual = Equals(original.Item1, copy.Item1);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 1, \"{copy}\", must equal original value, \"{original}\"");
            isEqual = Equals(original.Item2, copy.Item2);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 2, \"{copy}\", must equal original value, \"{original}\"");
            isEqual = Equals(original.Item3, copy.Item3);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 3, \"{copy}\", must equal original value, \"{original}\"");
            isEqual = Equals(original.Item4, copy.Item4);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 4, \"{copy}\", must equal original value, \"{original}\"");
        }

        /// <summary>
        /// Checks if object-typed tuples containing the field type can be copied.
        /// </summary>
        [Fact]
        public void CanCopyUntypedTupleViaSerializer()
        {
            var copier = _serviceProvider.GetRequiredService<DeepCopier<(string, object, object, string)>>();
            var value = ((IEnumerable<TValue>)TestValues).Reverse().Concat(new[] { CreateValue(), CreateValue() }).Take(2).ToArray();

            var original = (Guid.NewGuid().ToString(), (object)value[0], (object)value[1], Guid.NewGuid().ToString());

            var copy = copier.Copy(original);

            var isEqual = Equals(original.Item1, copy.Item1);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 1, \"{copy.Item1}\", must equal original value, \"{original.Item1}\"");
            isEqual = Equals((TValue)original.Item2, (TValue)copy.Item2);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 2, \"{copy.Item2}\", must equal original value, \"{original.Item2}\"");
            isEqual = Equals((TValue)original.Item3, (TValue)copy.Item3);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 3, \"{copy.Item3}\", must equal original value, \"{original.Item3}\"");
            isEqual = Equals(original.Item4, copy.Item4);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Copied value for item 4, \"{copy.Item4}\", must equal original value, \"{original.Item4}\"");
        }

        /// <summary>
        /// Checks if values can be round-tripped when used as an element in a strongly-typed list.
        /// </summary>
        [Fact]
        public void CanCopyCollectionViaSerializer()
        {
            var copier = _serviceProvider.GetRequiredService<DeepCopier<List<TValue>>>();

            var original = new List<TValue>();
            original.AddRange(TestValues);
            for (var i = 0; i < 5; i++)
            {
                original.Add(CreateValue());
            }

            var copy = copier.Copy(original);

            Assert.Equal(original.Count, copy.Count);
            for (var i = 0; i < original.Count; ++i)
            {
                var isEqual = Equals(original[i], copy[i]);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Copied value at index {i}, \"{copy}\", must equal original value, \"{original}\"");
            }
        }

        /// <summary>
        /// Checks if values can be round-tripped when used as an element in a list of object.
        /// </summary>
        [Fact]
        public void CanCopyCollectionViaUntypedSerializer()
        {
            var copier = _serviceProvider.GetRequiredService<DeepCopier<List<object>>>();

            var original = new List<object>();
            foreach (var value in TestValues)
            {
                original.Add(value);
            }

            for (var i = 0; i < 5; i++)
            {
                original.Add(CreateValue());
            }

            var copy = copier.Copy(original);

            Assert.Equal(original.Count, copy.Count);
            for (var i = 0; i < original.Count; ++i)
            {
                var isEqual = Equals((TValue)original[i], (TValue)copy[i]);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Copied value at index {i}, \"{copy}\", must equal original value, \"{original}\"");
            }
        }
    }
}
