using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Internal;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    /// <summary>
    /// Extensions for <see cref="IServiceCollection"/>.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds serializer support.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddSerializer(this IServiceCollection services, Action<ISerializerBuilder>? configure = null)
        {
            var context = GetOrCreateConfigurationContext(services, static builder =>
            {
                foreach (var asm in ReferencedAssemblyProvider.GetRelevantAssemblies())
                {
                    builder.AddAssembly(asm);
                }

                builder.Services.AddSingleton<IGeneralizedCodec, WellKnownStringComparerCodec>();
                builder.Services.AddSingleton<IGeneralizedCodec, InterfaceCollectionCodecResolver>();
                builder.Services.AddSingleton<ExceptionCodec>();
                builder.Services.AddSingleton<IGeneralizedCodec>(sp => sp.GetRequiredService<ExceptionCodec>());
                builder.Services.AddSingleton<IGeneralizedBaseCodec>(sp => sp.GetRequiredService<ExceptionCodec>());
            });
            configure?.Invoke(context.Builder);
            return services;
        }

        /// <summary>
        /// Adds the Orleans serializer and deep copier using an explicit, compile-time type graph.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="context">The generated serializer context.</param>
        /// <returns>The service collection.</returns>
        /// <remarks>Repeated calls combine the registered contexts. Closed factories take priority over metadata-based resolution.</remarks>
        public static IServiceCollection AddSerializerContext(this IServiceCollection services, SerializerContext context)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (context is null) throw new ArgumentNullException(nameof(context));
            GetOrCreateConfigurationContext(services, initialize: null).Builder.AddSerializerContext(context);
            return services;
        }

        private static ConfigurationContext GetOrCreateConfigurationContext(
            IServiceCollection services, Action<ISerializerBuilder>? initialize)
        {
            // Only add the services once.
            var context = GetFromServices<ConfigurationContext>(services);
            if (context is null)
            {
                context = new ConfigurationContext(services);
                initialize?.Invoke(context.Builder);
                context.AutomaticInitialized = initialize is not null;

                services.Add(context.CreateServiceDescriptor());
                services.AddOptions();
                services.AddSingleton<IConfigureOptions<TypeManifestOptions>, DefaultTypeManifestProvider>();
                services.AddSingleton<IPostConfigureOptions<TypeManifestOptions>, DefaultTypeManifestProvider>();
                services.AddSingleton<TypeResolver>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<TypeManifestOptions>>();
                    return new CachedTypeResolver(options.Value.ContextTypes);
                });
                services.AddSingleton<TypeConverter>();
                services.TryAddSingleton<CodecProvider>();
                services.TryAddSingleton<ICodecProvider>(sp => sp.GetRequiredService<CodecProvider>());
                services.TryAddSingleton<IDeepCopierProvider>(sp => sp.GetRequiredService<CodecProvider>());
                services.TryAddSingleton<IFieldCodecProvider>(sp => sp.GetRequiredService<CodecProvider>());
                services.TryAddSingleton<IBaseCodecProvider>(sp => sp.GetRequiredService<CodecProvider>());
                services.TryAddSingleton<IValueSerializerProvider>(sp => sp.GetRequiredService<CodecProvider>());
                services.TryAddSingleton<IActivatorProvider>(sp => sp.GetRequiredService<CodecProvider>());
                services.TryAddSingleton(typeof(IFieldCodec<>), typeof(FieldCodecHolder<>));
                services.TryAddSingleton(typeof(IBaseCodec<>), typeof(BaseCodecHolder<>));
                services.TryAddSingleton(typeof(IValueSerializer<>), typeof(ValueSerializerHolder<>));
                services.TryAddSingleton(typeof(IActivator<>), typeof(ActivatorHolder<>));
                services.TryAddSingleton<WellKnownTypeCollection>();
                services.TryAddSingleton<TypeCodec>();
                services.TryAddSingleton(typeof(IDeepCopier<>), typeof(CopierHolder<>));
                services.TryAddSingleton(typeof(IBaseCopier<>), typeof(BaseCopierHolder<>));

                // Type filtering
                services.AddSingleton<ITypeNameFilter, DefaultTypeFilter>();

                // Session
                services.TryAddSingleton<SerializerSessionPool>();
                services.TryAddSingleton<CopyContextPool>();

                // Serializer
                services.TryAddSingleton<ObjectSerializer>();
                services.TryAddSingleton<Serializer>();
                services.TryAddSingleton(typeof(Serializer<>));
                services.TryAddSingleton(typeof(ValueSerializer<>));
                services.TryAddSingleton<DeepCopier>();
                services.TryAddSingleton(typeof(DeepCopier<>));
            }
            else if (initialize is not null && !context.AutomaticInitialized)
            {
                initialize(context.Builder);
                context.AutomaticInitialized = true;
            }

            return context;
        }

        private static T? GetFromServices<T>(IServiceCollection services) where T : class
        {
            foreach (var service in services)
            {
                if (service.ServiceType == typeof(T))
                {
                    return (T?)service.ImplementationInstance;
                }
            }

            return default;
        }

        private sealed class ConfigurationContext
        {
            public ConfigurationContext(IServiceCollection services) => Builder = new SerializerBuilder(services);
            public bool AutomaticInitialized { get; set; }

            public ServiceDescriptor CreateServiceDescriptor() => new ServiceDescriptor(typeof(ConfigurationContext), this);

            public ISerializerBuilder Builder { get; }
        }

        private class SerializerBuilder : ISerializerBuilder
        {
            public SerializerBuilder(IServiceCollection services) => Services = services;

            public IServiceCollection Services { get; }
        }

        internal static Type? GetServiceHolderType(Type serviceType)
        {
            if (!serviceType.IsConstructedGenericType) return null;
            var definition = serviceType.GetGenericTypeDefinition();
            if (definition == typeof(IFieldCodec<>)) return typeof(FieldCodecHolder<>);
            if (definition == typeof(IBaseCodec<>)) return typeof(BaseCodecHolder<>);
            if (definition == typeof(IValueSerializer<>)) return typeof(ValueSerializerHolder<>);
            if (definition == typeof(IActivator<>)) return typeof(ActivatorHolder<>);
            if (definition == typeof(IDeepCopier<>)) return typeof(CopierHolder<>);
            if (definition == typeof(IBaseCopier<>)) return typeof(BaseCopierHolder<>);
            return null;
        }

        private sealed class ActivatorHolder<T> : IActivator<T>, IServiceHolder<IActivator<T>>
        {
            private readonly IActivatorProvider _activatorProvider;
            private IActivator<T>? _activator;

            public ActivatorHolder(IActivatorProvider codecProvider)
            {
                _activatorProvider = codecProvider;
            }

            public IActivator<T> Value => _activator ?? CacheCompleted(_activatorProvider, _activatorProvider.GetActivator<T>(), ref _activator);
            public object Provider => _activatorProvider;

            public T Create() => Value.Create();
        }

        internal sealed class FieldCodecHolder<TField> : IFieldCodec<TField>, IServiceHolder<IFieldCodec<TField>>
        {
            private readonly IFieldCodecProvider _codecProvider;
            private IFieldCodec<TField>? _codec;

            public FieldCodecHolder(IFieldCodecProvider codecProvider)
            {
                _codecProvider = codecProvider;
            }

            public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] TField value) where TBufferWriter : IBufferWriter<byte> => Value.WriteField(ref writer, fieldIdDelta, expectedType, value);

            [return: System.Diagnostics.CodeAnalysis.MaybeNull]
            public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field) => Value.ReadValue(ref reader, field);

            public IFieldCodec<TField> Value => _codec ?? CacheCompleted(_codecProvider, _codecProvider.GetCodec<TField>(), ref _codec);
            public object Provider => _codecProvider;
        }

        private sealed class BaseCodecHolder<TField> : IBaseCodec<TField>, IServiceHolder<IBaseCodec<TField>> where TField : class
        {
            private readonly IBaseCodecProvider _provider;
            private IBaseCodec<TField>? _baseCodec;

            public BaseCodecHolder(IBaseCodecProvider provider)
            {
                _provider = provider;
            }

            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, TField value) where TBufferWriter : IBufferWriter<byte> => Value.Serialize(ref writer, value);

            public void Deserialize<TInput>(ref Reader<TInput> reader, TField value) => Value.Deserialize(ref reader, value);

            public IBaseCodec<TField> Value => _baseCodec ?? CacheCompleted(_provider, _provider.GetBaseCodec<TField>(), ref _baseCodec);
            public object Provider => _provider;
        }

        private sealed class ValueSerializerHolder<TField> : IValueSerializer<TField>, IServiceHolder<IValueSerializer<TField>> where TField : struct
        {
            private readonly IValueSerializerProvider _provider;
            private IValueSerializer<TField>? _serializer;

            public ValueSerializerHolder(IValueSerializerProvider provider)
            {
                _provider = provider;
            }

            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, scoped ref TField value) where TBufferWriter : IBufferWriter<byte> => Value.Serialize(ref writer, ref value);

            public void Deserialize<TInput>(ref Reader<TInput> reader, scoped ref TField value) => Value.Deserialize(ref reader, ref value);

            public IValueSerializer<TField> Value => _serializer ?? CacheCompleted(_provider, _provider.GetValueSerializer<TField>(), ref _serializer);
            public object Provider => _provider;
        }

        internal sealed class CopierHolder<T> : IDeepCopier<T>, IServiceHolder<IDeepCopier<T>>, IOptionalDeepCopier
        {
            private readonly IDeepCopierProvider _codecProvider;
            private IDeepCopier<T>? _copier;

            public CopierHolder(IDeepCopierProvider codecProvider)
            {
                _codecProvider = codecProvider;
            }

            [return: NotNullIfNotNull(nameof(original))]
            public T? DeepCopy(T? original, CopyContext context) => Value.DeepCopy(original, context);

            [return: NotNullIfNotNull(nameof(original))]
            public object? DeepCopy(object? original, CopyContext context) => ((IDeepCopier)Value).DeepCopy(original, context);

            public bool IsShallowCopyable() => (Value as IOptionalDeepCopier)?.IsShallowCopyable() ?? false;

            public IDeepCopier<T> Value => _copier ?? CacheCompleted(_codecProvider, _codecProvider.GetDeepCopier<T>(), ref _copier);
            public object Provider => _codecProvider;
        }

        private sealed class BaseCopierHolder<T> : IBaseCopier<T>, IServiceHolder<IBaseCopier<T>> where T : class
        {
            private readonly IDeepCopierProvider _codecProvider;
            private IBaseCopier<T>? _copier;

            public BaseCopierHolder(IDeepCopierProvider codecProvider)
            {
                _codecProvider = codecProvider;
            }

            public void DeepCopy(T original, T copy, CopyContext context) => Value.DeepCopy(original, copy, context);

            public IBaseCopier<T> Value => _copier ?? CacheCompleted(_codecProvider, _codecProvider.GetBaseCopier<T>(), ref _copier);
            public object Provider => _codecProvider;
        }

        private static TService CacheCompleted<TService>(object provider, TService value, ref TService? slot) where TService : class
        {
            if (provider is not CodecProvider codecs || !codecs.IsConstructionPending) slot = value;
            return value;
        }
    }

    /// <summary>
    /// Holds a reference to a service.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal interface IServiceHolder<out T>
    {
        /// <summary>
        /// Gets the service.
        /// </summary>
        /// <value>The service.</value>
        T Value { get; }

        /// <summary>
        /// Gets the provider which resolves the held service.
        /// </summary>
        object Provider { get; }
    }
}
