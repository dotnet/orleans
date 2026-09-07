using System;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Base class for serialization test helpers.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public abstract class SerializationTester : IDisposable
    {
        private readonly bool _ownsServiceProvider;
        private readonly Lazy<IServiceProvider> _serviceProvider;

        /// <summary>
        /// Initializes a new <see cref="SerializationTester"/> instance.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
        protected SerializationTester(ITestOutputHelper output)
        {
            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            RandomSeed = CreateRandomSeed();
            Random = new(RandomSeed);
            _serviceProvider = new(CreateServiceProvider);
            _ownsServiceProvider = true;
        }

        /// <summary>
        /// Initializes a new <see cref="SerializationTester"/> instance.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="output"/> or <paramref name="fixture"/> is <see langword="null"/>.</exception>
        protected SerializationTester(ITestOutputHelper output, SerializationTesterFixture fixture)
        {
            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (fixture is null)
            {
                throw new ArgumentNullException(nameof(fixture));
            }

            RandomSeed = CreateRandomSeed();
            Random = new(RandomSeed);
            _serviceProvider = fixture.GetOrCreateServiceProvider(CreateServiceProvider);
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
        protected IServiceProvider ServiceProvider => _serviceProvider.Value;

        /// <summary>
        /// Creates the serializer service provider for this test class.
        /// </summary>
        protected abstract IServiceProvider CreateServiceProvider();

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _ownsServiceProvider && _serviceProvider.IsValueCreated)
            {
                (_serviceProvider.Value as IDisposable)?.Dispose();
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
        private Lazy<IServiceProvider>? _serviceProvider;

        /// <summary>
        /// Initializes a new <see cref="SerializationTesterFixture"/> instance.
        /// </summary>
        public SerializationTesterFixture()
        {
        }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        public IServiceProvider ServiceProvider => _serviceProvider?.Value ?? throw new InvalidOperationException("The service provider has not been initialized.");

        internal Lazy<IServiceProvider> GetOrCreateServiceProvider(Func<IServiceProvider> factory)
        {
            if (_serviceProvider is { } serviceProvider)
            {
                return serviceProvider;
            }

            lock (_lock)
            {
                return _serviceProvider ??= new(factory);
            }
        }

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _serviceProvider is { IsValueCreated: true } serviceProvider)
            {
                (serviceProvider.Value as IDisposable)?.Dispose();
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
