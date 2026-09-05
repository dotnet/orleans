using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class SerializerConstructorAndRegistrationTests
{
    [Fact]
    public void MessagePackCodec_NullSerializableSelectors_ThrowsWithExactParamNameBeforeUsingOtherDependencies()
        => AssertOptionsCodecRejectsNullSerializableSelectors(
            (serializable, copyable, options) => new MessagePackCodec(serializable, copyable, options),
            new MessagePackCodecOptions());

    [Fact]
    public void MessagePackCodec_NullCopyableSelectors_ThrowsWithExactParamNameBeforeUsingOtherDependencies()
        => AssertOptionsCodecRejectsNullCopyableSelectors(
            (serializable, copyable, options) => new MessagePackCodec(serializable, copyable, options),
            new MessagePackCodecOptions());

    [Fact]
    public void MessagePackCodec_NullOptions_ThrowsWithExactParamNameBeforeEnumeratingSelectors()
        => AssertOptionsCodecRejectsNullOptions<MessagePackCodecOptions>(
            (serializable, copyable, options) => new MessagePackCodec(serializable, copyable, options));

    [Fact]
    public void MessagePackCodec_ValidDependencies_RetainsSelectorAndOptionsSemantics()
    {
        var observations = new CodecConstructionObservations();
        var options = new MessagePackCodecOptions
        {
            IsSerializableType = observations.IsSerializableByOptions,
            IsCopyableType = observations.IsCopyableByOptions,
        };

        var codec = new MessagePackCodec(
            observations.CreateCodecSelectors(MessagePackCodec.WellKnownAlias),
            observations.CreateCopierSelectors(MessagePackCodec.WellKnownAlias),
            Options.Create(options));

        AssertValidOptionsCodecSemantics(codec, observations);
    }

    [Fact]
    public void NewtonsoftJsonCodec_NullSerializableSelectors_ThrowsWithExactParamNameBeforeUsingOtherDependencies()
        => AssertOptionsCodecRejectsNullSerializableSelectors(
            (serializable, copyable, options) => new NewtonsoftJsonCodec(serializable, copyable, options),
            new NewtonsoftJsonCodecOptions());

    [Fact]
    public void NewtonsoftJsonCodec_NullCopyableSelectors_ThrowsWithExactParamNameBeforeUsingOtherDependencies()
        => AssertOptionsCodecRejectsNullCopyableSelectors(
            (serializable, copyable, options) => new NewtonsoftJsonCodec(serializable, copyable, options),
            new NewtonsoftJsonCodecOptions());

    [Fact]
    public void NewtonsoftJsonCodec_NullOptions_ThrowsWithExactParamNameBeforeEnumeratingSelectors()
        => AssertOptionsCodecRejectsNullOptions<NewtonsoftJsonCodecOptions>(
            (serializable, copyable, options) => new NewtonsoftJsonCodec(serializable, copyable, options));

    [Fact]
    public void NewtonsoftJsonCodec_ValidDependencies_RetainsSelectorAndOptionsSemantics()
    {
        var observations = new CodecConstructionObservations();
        var options = new NewtonsoftJsonCodecOptions
        {
            IsSerializableType = observations.IsSerializableByOptions,
            IsCopyableType = observations.IsCopyableByOptions,
        };

        var codec = new NewtonsoftJsonCodec(
            observations.CreateCodecSelectors(NewtonsoftJsonCodec.WellKnownAlias),
            observations.CreateCopierSelectors(NewtonsoftJsonCodec.WellKnownAlias),
            Options.Create(options));

        AssertValidOptionsCodecSemantics(codec, observations);
    }

    [Fact]
    public void JsonCodec_NullSerializableSelectors_ThrowsWithExactParamNameBeforeUsingOtherDependencies()
        => AssertOptionsCodecRejectsNullSerializableSelectors(
            (serializable, copyable, options) => new JsonCodec(serializable, copyable, options),
            new JsonCodecOptions());

    [Fact]
    public void JsonCodec_NullCopyableSelectors_ThrowsWithExactParamNameBeforeUsingOtherDependencies()
        => AssertOptionsCodecRejectsNullCopyableSelectors(
            (serializable, copyable, options) => new JsonCodec(serializable, copyable, options),
            new JsonCodecOptions());

    [Fact]
    public void JsonCodec_NullOptions_ThrowsWithExactParamNameBeforeEnumeratingSelectors()
        => AssertOptionsCodecRejectsNullOptions<JsonCodecOptions>(
            (serializable, copyable, options) => new JsonCodec(serializable, copyable, options));

    [Fact]
    public void JsonCodec_ValidDependencies_RetainsSelectorAndOptionsSemantics()
    {
        var observations = new CodecConstructionObservations();
        var options = new JsonCodecOptions
        {
            IsSerializableType = observations.IsSerializableByOptions,
            IsCopyableType = observations.IsCopyableByOptions,
        };

        var codec = new JsonCodec(
            observations.CreateCodecSelectors(JsonCodec.WellKnownAlias),
            observations.CreateCopierSelectors(JsonCodec.WellKnownAlias),
            Options.Create(options));

        AssertValidOptionsCodecSemantics(codec, observations);
    }

    [Fact]
    public void ProtobufCodec_NullSerializableSelectors_ThrowsWithExactParamNameBeforeEnumeratingCopyableSelectors()
    {
        var copyable = new TrackingEnumerable<ICopierSelector>([]);

        var exception = Assert.Throws<ArgumentNullException>(() => new ProtobufCodec(null!, copyable));

        Assert.Equal("serializableTypeSelectors", exception.ParamName);
        Assert.Equal(0, copyable.EnumerationCount);
    }

    [Fact]
    public void ProtobufCodec_NullCopyableSelectors_ThrowsWithExactParamNameBeforeEnumeratingSerializableSelectors()
    {
        var serializable = new TrackingEnumerable<ICodecSelector>([]);

        var exception = Assert.Throws<ArgumentNullException>(() => new ProtobufCodec(serializable, null!));

        Assert.Equal("copyableTypeSelectors", exception.ParamName);
        Assert.Equal(0, serializable.EnumerationCount);
    }

    [Fact]
    public void ProtobufCodec_ValidDependencies_RetainsSelectorSemantics()
    {
        var serializableCallbackCount = 0;
        var copyableCallbackCount = 0;
        var unrelatedCallbackCount = 0;
        var codec = new ProtobufCodec(
            [
                new DelegateCodecSelector
                {
                    CodecName = "unrelated",
                    IsSupportedTypeDelegate = _ =>
                    {
                        unrelatedCallbackCount++;
                        return true;
                    },
                },
                new DelegateCodecSelector
                {
                    CodecName = ProtobufCodec.WellKnownAlias,
                    IsSupportedTypeDelegate = type =>
                    {
                        serializableCallbackCount++;
                        return type == typeof(MyProtobufClass);
                    },
                },
            ],
            [
                new DelegateCopierSelector
                {
                    CopierName = "unrelated",
                    IsSupportedTypeDelegate = _ =>
                    {
                        unrelatedCallbackCount++;
                        return true;
                    },
                },
                new DelegateCopierSelector
                {
                    CopierName = ProtobufCodec.WellKnownAlias,
                    IsSupportedTypeDelegate = type =>
                    {
                        copyableCallbackCount++;
                        return type == typeof(MyProtobufClass);
                    },
                },
            ]);

        Assert.True(((IGeneralizedCodec)codec).IsSupportedType(typeof(MyProtobufClass)));
        Assert.True(((IGeneralizedCopier)codec).IsSupportedType(typeof(MyProtobufClass)));
        Assert.Equal(1, serializableCallbackCount);
        Assert.Equal(1, copyableCallbackCount);
        Assert.Equal(0, unrelatedCallbackCount);
    }

    [Fact]
    public void AddMessagePackSerializer_NullBuilder_ThrowsBeforeInvokingCallbacks()
    {
        ISerializerBuilder builder = null!;
        var callbacks = new RegistrationCallbackObservations();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.AddMessagePackSerializer(
                callbacks.IsSerializable,
                callbacks.IsCopyable,
                _ => callbacks.ConfigureCount++));

        Assert.Equal("serializerBuilder", exception.ParamName);
        callbacks.AssertNotInvoked();
    }

    [Fact]
    public void AddNewtonsoftJsonSerializer_NullBuilder_ThrowsBeforeInvokingCallbacks()
    {
        ISerializerBuilder builder = null!;
        var callbacks = new RegistrationCallbackObservations();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.AddNewtonsoftJsonSerializer(
                callbacks.IsSerializable,
                callbacks.IsCopyable,
                _ => callbacks.ConfigureCount++));

        Assert.Equal("serializerBuilder", exception.ParamName);
        callbacks.AssertNotInvoked();
    }

    [Fact]
    public void AddJsonSerializer_NullBuilder_ThrowsBeforeInvokingCallbacks()
    {
        ISerializerBuilder builder = null!;
        var callbacks = new RegistrationCallbackObservations();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.AddJsonSerializer(
                callbacks.IsSerializable,
                callbacks.IsCopyable,
                _ => callbacks.ConfigureCount++));

        Assert.Equal("serializerBuilder", exception.ParamName);
        callbacks.AssertNotInvoked();
    }

    [Fact]
    public void AddProtobufSerializer_NullBuilder_ThrowsBeforeInvokingSelectorCallbacks()
    {
        ISerializerBuilder builder = null!;
        var callbacks = new RegistrationCallbackObservations();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.AddProtobufSerializer(callbacks.IsSerializable, callbacks.IsCopyable));

        Assert.Equal("serializerBuilder", exception.ParamName);
        callbacks.AssertSelectorsNotInvoked();
    }

    [Fact]
    public void AddProtobufSerializer_NullSerializableSelector_ThrowsBeforeCallbacksOrServiceMutation()
    {
        var builder = CreateBuilder(out var services);
        var callbacks = new RegistrationCallbackObservations();
        var initialServiceCount = services.Count;

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.AddProtobufSerializer(null!, callbacks.IsCopyable));

        Assert.Equal("isSerializable", exception.ParamName);
        callbacks.AssertSelectorsNotInvoked();
        Assert.Equal(initialServiceCount, services.Count);
    }

    [Fact]
    public void AddProtobufSerializer_NullCopyableSelector_ThrowsBeforeCallbacksOrServiceMutation()
    {
        var builder = CreateBuilder(out var services);
        var callbacks = new RegistrationCallbackObservations();
        var initialServiceCount = services.Count;

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.AddProtobufSerializer(callbacks.IsSerializable, null!));

        Assert.Equal("isCopyable", exception.ParamName);
        callbacks.AssertSelectorsNotInvoked();
        Assert.Equal(initialServiceCount, services.Count);
    }

    [Fact]
    public void AddMessagePackSerializer_ValidInputs_RegistersCodecSelectorsOptionsAndAlias()
    {
        var builder = CreateBuilder(out var services);
        var callbacks = new RegistrationCallbackObservations();

        var result = builder.AddMessagePackSerializer(
            callbacks.IsSerializable,
            callbacks.IsCopyable,
            optionsBuilder =>
            {
                callbacks.ConfigureCount++;
                optionsBuilder.Configure(options => options.AllowDataContractAttributes = true);
            });

        Assert.Same(builder, result);
        Assert.Equal(1, callbacks.ConfigureCount);
        callbacks.AssertSelectorsNotInvoked();
        AssertValidRegistration<MessagePackCodec>(services, MessagePackCodec.WellKnownAlias, callbacks);

        using var serviceProvider = services.BuildServiceProvider();
        Assert.True(serviceProvider.GetRequiredService<IOptions<MessagePackCodecOptions>>().Value.AllowDataContractAttributes);
    }

    [Fact]
    public void AddNewtonsoftJsonSerializer_ValidInputs_RegistersCodecSelectorsOptionsAndAlias()
    {
        var builder = CreateBuilder(out var services);
        var callbacks = new RegistrationCallbackObservations();

        var result = builder.AddNewtonsoftJsonSerializer(
            callbacks.IsSerializable,
            callbacks.IsCopyable,
            optionsBuilder =>
            {
                callbacks.ConfigureCount++;
                optionsBuilder.Configure(options => options.IsSerializableType = _ => true);
            });

        Assert.Same(builder, result);
        Assert.Equal(1, callbacks.ConfigureCount);
        callbacks.AssertSelectorsNotInvoked();
        AssertValidRegistration<NewtonsoftJsonCodec>(services, NewtonsoftJsonCodec.WellKnownAlias, callbacks);

        using var serviceProvider = services.BuildServiceProvider();
        Assert.True(serviceProvider.GetRequiredService<IOptions<NewtonsoftJsonCodecOptions>>().Value.IsSerializableType!(typeof(OptionsSelectedType)));
    }

    [Fact]
    public void AddJsonSerializer_ValidInputs_RegistersCodecSelectorsOptionsAndAlias()
    {
        var builder = CreateBuilder(out var services);
        var callbacks = new RegistrationCallbackObservations();

        var result = builder.AddJsonSerializer(
            callbacks.IsSerializable,
            callbacks.IsCopyable,
            optionsBuilder =>
            {
                callbacks.ConfigureCount++;
                optionsBuilder.Configure(options => options.IsCopyableType = _ => true);
            });

        Assert.Same(builder, result);
        Assert.Equal(1, callbacks.ConfigureCount);
        callbacks.AssertSelectorsNotInvoked();
        AssertValidRegistration<JsonCodec>(services, JsonCodec.WellKnownAlias, callbacks);

        using var serviceProvider = services.BuildServiceProvider();
        Assert.True(serviceProvider.GetRequiredService<IOptions<JsonCodecOptions>>().Value.IsCopyableType!(typeof(OptionsSelectedType)));
    }

    [Fact]
    public void AddProtobufSerializer_ValidInputs_RegistersCodecSelectorsAndAlias()
    {
        var builder = CreateBuilder(out var services);
        var callbacks = new RegistrationCallbackObservations();

        var result = builder.AddProtobufSerializer(callbacks.IsSerializable, callbacks.IsCopyable);

        Assert.Same(builder, result);
        callbacks.AssertSelectorsNotInvoked();
        AssertValidRegistration<ProtobufCodec>(services, ProtobufCodec.WellKnownAlias, callbacks);
    }

    private static void AssertOptionsCodecRejectsNullSerializableSelectors<TOptions>(
        Func<IEnumerable<ICodecSelector>, IEnumerable<ICopierSelector>, IOptions<TOptions>, object> factory,
        TOptions validOptions)
        where TOptions : class
    {
        var copyable = new TrackingEnumerable<ICopierSelector>([]);
        var options = new TrackingOptions<TOptions>(validOptions);

        var exception = Assert.Throws<ArgumentNullException>(() => factory(null!, copyable, options));

        Assert.Equal("serializableTypeSelectors", exception.ParamName);
        Assert.Equal(0, copyable.EnumerationCount);
        Assert.Equal(0, options.ValueAccessCount);
    }

    private static void AssertOptionsCodecRejectsNullCopyableSelectors<TOptions>(
        Func<IEnumerable<ICodecSelector>, IEnumerable<ICopierSelector>, IOptions<TOptions>, object> factory,
        TOptions validOptions)
        where TOptions : class
    {
        var serializable = new TrackingEnumerable<ICodecSelector>([]);
        var options = new TrackingOptions<TOptions>(validOptions);

        var exception = Assert.Throws<ArgumentNullException>(() => factory(serializable, null!, options));

        Assert.Equal("copyableTypeSelectors", exception.ParamName);
        Assert.Equal(0, serializable.EnumerationCount);
        Assert.Equal(0, options.ValueAccessCount);
    }

    private static void AssertOptionsCodecRejectsNullOptions<TOptions>(
        Func<IEnumerable<ICodecSelector>, IEnumerable<ICopierSelector>, IOptions<TOptions>, object> factory)
        where TOptions : class
    {
        var serializable = new TrackingEnumerable<ICodecSelector>([]);
        var copyable = new TrackingEnumerable<ICopierSelector>([]);

        var exception = Assert.Throws<ArgumentNullException>(() => factory(serializable, copyable, null!));

        Assert.Equal("options", exception.ParamName);
        Assert.Equal(0, serializable.EnumerationCount);
        Assert.Equal(0, copyable.EnumerationCount);
    }

    private static void AssertValidOptionsCodecSemantics(
        object codec,
        CodecConstructionObservations observations)
    {
        Assert.True(((IGeneralizedCodec)codec).IsSupportedType(typeof(SelectorSelectedType)));
        Assert.True(((IGeneralizedCopier)codec).IsSupportedType(typeof(SelectorSelectedType)));
        Assert.True(((IGeneralizedCodec)codec).IsSupportedType(typeof(OptionsSelectedType)));
        Assert.True(((IGeneralizedCopier)codec).IsSupportedType(typeof(OptionsSelectedType)));

        Assert.Equal(2, observations.SerializableSelectorCount);
        Assert.Equal(2, observations.CopyableSelectorCount);
        Assert.Equal(1, observations.SerializableOptionsCount);
        Assert.Equal(1, observations.CopyableOptionsCount);
        Assert.Equal(0, observations.UnrelatedSelectorCount);
    }

    private static ISerializerBuilder CreateBuilder(out ServiceCollection services)
    {
        services = new ServiceCollection();
        services.AddOptions();
        return new TestSerializerBuilder(services);
    }

    private static void AssertValidRegistration<TCodec>(
        ServiceCollection services,
        string alias,
        RegistrationCallbackObservations callbacks)
    {
        var codecRegistration = Assert.Single(services, service => service.ServiceType == typeof(TCodec));
        Assert.Equal(ServiceLifetime.Singleton, codecRegistration.Lifetime);
        Assert.Equal(typeof(TCodec), codecRegistration.ImplementationType);
        Assert.Single(services, service => service.ServiceType == typeof(IGeneralizedCodec));
        Assert.Single(services, service => service.ServiceType == typeof(IGeneralizedCopier));
        Assert.Single(services, service => service.ServiceType == typeof(ITypeFilter));

        var codecSelector = Assert.IsType<DelegateCodecSelector>(
            Assert.Single(services, service => service.ServiceType == typeof(ICodecSelector)).ImplementationInstance);
        var copierSelector = Assert.IsType<DelegateCopierSelector>(
            Assert.Single(services, service => service.ServiceType == typeof(ICopierSelector)).ImplementationInstance);

        Assert.Equal(alias, codecSelector.CodecName);
        Assert.Equal(alias, copierSelector.CopierName);
        Assert.True(codecSelector.IsSupportedType(typeof(RegistrationSelectedType)));
        Assert.True(copierSelector.IsSupportedType(typeof(RegistrationSelectedType)));
        Assert.Equal(1, callbacks.SerializableSelectorCount);
        Assert.Equal(1, callbacks.CopyableSelectorCount);

        using var serviceProvider = services.BuildServiceProvider();
        var manifest = serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
        Assert.Equal(typeof(TCodec), manifest.WellKnownTypeAliases[alias]);
    }

    private sealed class TestSerializerBuilder(IServiceCollection services) : ISerializerBuilder
    {
        public IServiceCollection Services { get; } = services;
    }

    private sealed class TrackingEnumerable<T>(T[] values) : IEnumerable<T>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return ((IEnumerable<T>)values).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TrackingOptions<T>(T value) : IOptions<T>
        where T : class
    {
        public int ValueAccessCount { get; private set; }

        public T Value
        {
            get
            {
                ValueAccessCount++;
                return value;
            }
        }
    }

    private sealed class CodecConstructionObservations
    {
        public int SerializableSelectorCount { get; private set; }

        public int CopyableSelectorCount { get; private set; }

        public int SerializableOptionsCount { get; private set; }

        public int CopyableOptionsCount { get; private set; }

        public int UnrelatedSelectorCount { get; private set; }

        public ICodecSelector[] CreateCodecSelectors(string alias) =>
        [
            new DelegateCodecSelector
            {
                CodecName = "unrelated",
                IsSupportedTypeDelegate = _ =>
                {
                    UnrelatedSelectorCount++;
                    return true;
                },
            },
            new DelegateCodecSelector
            {
                CodecName = alias,
                IsSupportedTypeDelegate = type =>
                {
                    SerializableSelectorCount++;
                    return type == typeof(SelectorSelectedType);
                },
            },
        ];

        public ICopierSelector[] CreateCopierSelectors(string alias) =>
        [
            new DelegateCopierSelector
            {
                CopierName = "unrelated",
                IsSupportedTypeDelegate = _ =>
                {
                    UnrelatedSelectorCount++;
                    return true;
                },
            },
            new DelegateCopierSelector
            {
                CopierName = alias,
                IsSupportedTypeDelegate = type =>
                {
                    CopyableSelectorCount++;
                    return type == typeof(SelectorSelectedType);
                },
            },
        ];

        public bool? IsSerializableByOptions(Type type)
        {
            SerializableOptionsCount++;
            return type == typeof(OptionsSelectedType);
        }

        public bool? IsCopyableByOptions(Type type)
        {
            CopyableOptionsCount++;
            return type == typeof(OptionsSelectedType);
        }
    }

    private sealed class RegistrationCallbackObservations
    {
        public int SerializableSelectorCount { get; private set; }

        public int CopyableSelectorCount { get; private set; }

        public int ConfigureCount { get; set; }

        public bool IsSerializable(Type type)
        {
            SerializableSelectorCount++;
            return type == typeof(RegistrationSelectedType);
        }

        public bool IsCopyable(Type type)
        {
            CopyableSelectorCount++;
            return type == typeof(RegistrationSelectedType);
        }

        public void AssertNotInvoked()
        {
            AssertSelectorsNotInvoked();
            Assert.Equal(0, ConfigureCount);
        }

        public void AssertSelectorsNotInvoked()
        {
            Assert.Equal(0, SerializableSelectorCount);
            Assert.Equal(0, CopyableSelectorCount);
        }
    }

    private sealed class SelectorSelectedType;

    private sealed class OptionsSelectedType;

    private sealed class RegistrationSelectedType;
}
