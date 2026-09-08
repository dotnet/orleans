using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
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
        private readonly Lazy<IServiceProvider>? _serviceProvider;
        private readonly SerializationTesterFixture? _fixture;
        private int _disposed;

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
            _fixture = fixture;
            fixture.SetServiceProviderFactory(this);
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
        protected IServiceProvider ServiceProvider
        {
            get
            {
                if (_fixture is null)
                {
                    return _serviceProvider!.Value;
                }

                var serviceProvider = _fixture.ServiceProvider;
                GC.KeepAlive(this);
                return serviceProvider;
            }
        }

        /// <summary>
        /// Creates the serializer service provider for this test class.
        /// </summary>
        protected abstract IServiceProvider CreateServiceProvider();

        internal IServiceProvider CreateFixtureServiceProvider()
        {
            ThrowIfDisposed();
            return CreateServiceProvider();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _ownsServiceProvider && _serviceProvider!.IsValueCreated)
            {
                (_serviceProvider.Value as IDisposable)?.Dispose();
            }
        }

        /// <inheritdoc/>
        void IDisposable.Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

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
        private IServiceProvider? _serviceProvider;
        private WeakReference<SerializationTester>? _serviceProviderFactory;
        private bool _isCreatingServiceProvider;

        /// <summary>
        /// Initializes a new <see cref="SerializationTesterFixture"/> instance.
        /// </summary>
        public SerializationTesterFixture()
        {
        }

        /// <summary>
        /// Gets the service provider shared by tester instances using this fixture.
        /// Before creation, the most recently constructed tester supplies the service provider configuration.
        /// </summary>
        /// <exception cref="InvalidOperationException">No tester is available to create the service provider, or the service provider factory accesses this property recursively.</exception>
        /// <exception cref="ObjectDisposedException">The tester available to create the service provider has been disposed.</exception>
        public IServiceProvider ServiceProvider
        {
            get
            {
                lock (_lock)
                {
                    if (_serviceProvider is { } serviceProvider)
                    {
                        return serviceProvider;
                    }

                    if (_isCreatingServiceProvider)
                    {
                        throw new InvalidOperationException("The service provider factory cannot access the service provider while it is being created.");
                    }

                    if (_serviceProviderFactory is null || !_serviceProviderFactory.TryGetTarget(out var tester))
                    {
                        throw new InvalidOperationException("The serialization tester which configures the service provider is no longer available.");
                    }

                    _isCreatingServiceProvider = true;
                    try
                    {
                        return _serviceProvider = tester.CreateFixtureServiceProvider();
                    }
                    finally
                    {
                        _isCreatingServiceProvider = false;
                    }
                }
            }
        }

        internal void SetServiceProviderFactory(SerializationTester tester)
        {
            lock (_lock)
            {
                if (_serviceProvider is not null)
                {
                    return;
                }

                if (_serviceProviderFactory is null)
                {
                    _serviceProviderFactory = new(tester);
                }
                else
                {
                    _serviceProviderFactory.SetTarget(tester);
                }
            }
        }

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            IServiceProvider? serviceProvider;
            lock (_lock)
            {
                serviceProvider = _serviceProvider;
            }

            (serviceProvider as IDisposable)?.Dispose();
        }

        /// <inheritdoc/>
        void IDisposable.Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

    }
}
