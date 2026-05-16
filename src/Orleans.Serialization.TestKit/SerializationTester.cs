using System;
using System.Diagnostics.CodeAnalysis;
using Xunit.Abstractions;

#nullable disable
namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Base class for serialization test helpers.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public abstract class SerializationTester : IDisposable
    {
        private readonly bool _ownsServiceProvider;

        /// <summary>
        /// Initializes a new <see cref="SerializationTester"/> instance.
        /// </summary>
        protected SerializationTester(ITestOutputHelper output)
        {
            _ = output;
            RandomSeed = CreateRandomSeed();
            Random = new(RandomSeed);
            ServiceProvider = CreateServiceProvider();
            _ownsServiceProvider = true;
        }

        /// <summary>
        /// Initializes a new <see cref="SerializationTester"/> instance.
        /// </summary>
        protected SerializationTester(ITestOutputHelper output, SerializationTesterFixture fixture)
        {
            _ = output;
            if (fixture is null)
            {
                throw new ArgumentNullException(nameof(fixture));
            }

            RandomSeed = CreateRandomSeed();
            Random = new(RandomSeed);
            ServiceProvider = fixture.GetOrCreateServiceProvider(CreateServiceProvider);
        }

        private static int CreateRandomSeed()
        {
#if NET6_0_OR_GREATER
            return Random.Shared.Next();
#else
            return new Random().Next();
#endif
        }

        /// <summary>
        /// Gets the random number generator.
        /// </summary>
        protected Random Random { get; }

        internal int RandomSeed { get; }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        protected IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Creates the serializer service provider for this test class.
        /// </summary>
        protected abstract IServiceProvider CreateServiceProvider();

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _ownsServiceProvider)
            {
                (ServiceProvider as IDisposable)?.Dispose();
            }
        }

        /// <inheritdoc/>
        void IDisposable.Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Fixture which owns a serializer service provider shared by all instances of a test class.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public class SerializationTesterFixture : IDisposable
    {
        private readonly object _lock = new();
        private IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new <see cref="SerializationTesterFixture"/> instance.
        /// </summary>
        public SerializationTesterFixture()
        {
        }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        public IServiceProvider ServiceProvider => _serviceProvider ?? throw new InvalidOperationException("The service provider has not been initialized.");

        internal IServiceProvider GetOrCreateServiceProvider(Func<IServiceProvider> factory)
        {
            if (_serviceProvider is { } serviceProvider)
            {
                return serviceProvider;
            }

            lock (_lock)
            {
                return _serviceProvider ??= factory();
            }
        }

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                (_serviceProvider as IDisposable)?.Dispose();
            }
        }

        /// <inheritdoc/>
        void IDisposable.Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
