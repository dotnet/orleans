using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;

namespace Orleans.Serialization.TrimmedSmoke;

internal static class Program
{
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.NonPublicConstructors
        | DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.NonPublicMethods,
        typeof(SerializablePayload))]
    private static void Main()
    {
        using var serviceProvider = new ServiceCollection()
            .AddSerializer(builder => builder.Configure(options =>
            {
                options.AddSerializer(typeof(CustomGenericCodec<>));
                options.AddCopier(typeof(CustomGenericCopier<>));
                options.AddActivator(typeof(CustomGenericActivator<>));
            }))
            .AddSingleton<IGeneralizedCodec, DotNetSerializableCodec>()
            .BuildServiceProvider();

        var codecProvider = serviceProvider.GetRequiredService<CodecProvider>();
        ValidateGeneratedSerializer(serviceProvider);
        ValidateManualRegistrations(codecProvider);
        ValidateSerializableCallbacks(serviceProvider);
        ValidateGeneratedHelper(codecProvider);
        ValidateConfigurationAnalyzer(serviceProvider, codecProvider);
        ValidateGeneratedProxy(serviceProvider, codecProvider);
    }

    private static void ValidateManualRegistrations(CodecProvider codecProvider)
    {
        Ensure(
            codecProvider.GetCodec<CustomTarget<string>>().GetType() == typeof(CustomGenericCodec<string>),
            "The manually registered generic codec constructor was not preserved.");
        Ensure(
            codecProvider.GetDeepCopier<CustomTarget<string>>().GetType() == typeof(CustomGenericCopier<string>),
            "The manually registered generic copier constructor was not preserved.");
        Ensure(
            codecProvider.GetActivator<CustomTarget<string>>().GetType() == typeof(CustomGenericActivator<string>),
            "The manually registered generic activator constructor was not preserved.");
    }

    private static void ValidateGeneratedSerializer(IServiceProvider serviceProvider)
    {
        var serializer = serviceProvider.GetRequiredService<Serializer<GeneratedPayload<string>>>();
        var input = new GeneratedPayload<string>("trim-safe");

        var result = serializer.Deserialize(serializer.SerializeToArray(input))
            ?? throw new InvalidOperationException("Generated serializer returned null.");
        var copied = serviceProvider.GetRequiredService<DeepCopier>().Copy(input)
            ?? throw new InvalidOperationException("Generated copier returned null.");

        Ensure(result.Value == input.Value, "Generated serializer did not preserve a private field.");
        Ensure(copied.Value == input.Value, "Generated copier did not preserve a private field.");
    }

    private static void ValidateSerializableCallbacks(IServiceProvider serviceProvider)
    {
        SerializablePayload.ResetHistory();
        var serializer = serviceProvider.GetRequiredService<Serializer<object>>();
        var input = new SerializablePayload("payload", 17);

        var result = serializer.Deserialize(serializer.SerializeToArray(input)) as SerializablePayload
            ?? throw new InvalidOperationException("ISerializable deserialization returned an unexpected value.");

        Ensure(result.Payload == input.Payload, "ISerializable payload was not restored.");
        Ensure(result.Revision == input.Revision, "ISerializable revision was not restored.");
        Ensure(result.ConstructorRestoredState == "payload:17", "The non-public serialization constructor was not invoked.");
        Ensure(
            SerializablePayload.History.SequenceEqual(
            [
                "on_serializing",
                "get_object_data",
                "on_serialized",
                "on_deserializing",
                "serialization_ctor",
                "on_deserialized",
                "on_deserialization"
            ]),
            "ISerializable callbacks were not invoked in the expected order.");
    }

    private static void ValidateGeneratedHelper(CodecProvider codecProvider)
    {
        var service = OrleansGeneratedCodeHelper.GetService<PublicConstructorService>(new object(), codecProvider);
        Ensure(service.Value == 42, "Generated service resolution did not invoke the public constructor.");

        var method = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(
            typeof(ITrimSmokeGrain),
            nameof(ITrimSmokeGrain.Echo),
            methodTypeParameters: null,
            [typeof(GeneratedPayload<string>)]);
        Ensure(method is not null, "Generated method metadata was not preserved.");
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "TypeManifestOptions.AddInterfaceImplementation preserves implemented interfaces before the type flows through the manifest collection.")]
    private static void ValidateConfigurationAnalyzer(IServiceProvider serviceProvider, CodecProvider codecProvider)
    {
        var analysisOptions = new TypeManifestOptions();
        analysisOptions.AddInterface(typeof(ITrimSmokeGrain));
        var complaints = SerializerConfigurationAnalyzer.AnalyzeSerializerAvailability(codecProvider, analysisOptions);
        var manifestOptions = serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
        var proxyType = manifestOptions.InterfaceProxies.Single(type => typeof(ITrimSmokeGrain).IsAssignableFrom(type));

        Ensure(
            complaints.Keys.All(type => type != typeof(GeneratedPayload<string>)),
            "Serializer configuration analysis did not find the generated serializer and copier.");
        Ensure(
            typeof(ITrimSmokeGrain).IsAssignableFrom(proxyType),
            "The generated proxy's implemented grain interface was not preserved.");
        var implementationType = manifestOptions.InterfaceImplementations.Single(type => type == typeof(TrimSmokeGrain));
        Ensure(
            implementationType.GetInterfaces().Contains(typeof(ITrimSmokeGrain)),
            "The generated grain implementation's interface metadata was not preserved.");
    }

    private static void ValidateGeneratedProxy(IServiceProvider serviceProvider, CodecProvider codecProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
        var proxyType = options.InterfaceProxies.Single(type => typeof(ITrimSmokeGrain).IsAssignableFrom(type));
        var grainType = GrainType.Create("trim-smoke");
        var interfaceType = GrainInterfaceType.Create("trim-smoke-interface");
        var shared = new GrainReferenceShared(
            grainType,
            interfaceType,
            interfaceVersion: 0,
            runtime: null!,
            InvokeMethodOptions.None,
            codecProvider,
            serviceProvider.GetRequiredService<Orleans.Serialization.Cloning.CopyContextPool>(),
            serviceProvider);
        var referenceActivator = new Orleans.GrainReferences.GrainReferenceActivator(
            serviceProvider,
            [new TrimSmokeReferenceActivatorProvider(proxyType, shared)]);

        var proxy = referenceActivator.CreateReference(
            GrainId.Create(grainType, IdSpan.Create("key")),
            interfaceType);

        Ensure(proxy is ITrimSmokeGrain, "The generated grain proxy constructor was not preserved.");
    }

    private static void Ensure([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public interface ITrimSmokeGrain : IGrainWithStringKey
{
    Task<GeneratedPayload<string>> Echo(GeneratedPayload<string> value);
}

public sealed class TrimSmokeGrain : Grain, ITrimSmokeGrain
{
    public Task<GeneratedPayload<string>> Echo(GeneratedPayload<string> value) => Task.FromResult(value);
}

[GenerateSerializer]
public sealed class GeneratedPayload<T>
{
    [Id(0)]
    private T _value;

    public GeneratedPayload(T value)
    {
        _value = value;
    }

    public T Value => _value;
}

internal sealed class PublicConstructorService
{
    public PublicConstructorService()
    {
    }

    public int Value => 42;
}

internal sealed class CustomTarget<T>;

internal sealed class CustomGenericCodec<T> : Orleans.Serialization.Codecs.IFieldCodec<CustomTarget<T>>
{
    public CustomGenericCodec()
    {
    }

    public void WriteField<TBufferWriter>(
        ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        [AllowNull] Type expectedType,
        [AllowNull] CustomTarget<T> value)
        where TBufferWriter : System.Buffers.IBufferWriter<byte> =>
        throw new NotSupportedException("This codec is used only to verify registration activation.");

    public CustomTarget<T> ReadValue<TInput>(
        ref Orleans.Serialization.Buffers.Reader<TInput> reader,
        Orleans.Serialization.WireProtocol.Field field) =>
        throw new NotSupportedException("This codec is used only to verify registration activation.");
}

internal sealed class CustomGenericCopier<T> : Orleans.Serialization.Cloning.IDeepCopier<CustomTarget<T>>
{
    public CustomGenericCopier()
    {
    }

    public CustomTarget<T> DeepCopy(
        CustomTarget<T> input,
        Orleans.Serialization.Cloning.CopyContext context) => input;
}

internal sealed class CustomGenericActivator<T> : Orleans.Serialization.Activators.IActivator<CustomTarget<T>>
{
    public CustomGenericActivator()
    {
    }

    public CustomTarget<T> Create() => new();
}

internal sealed class TrimSmokeReferenceActivatorProvider(
    Type proxyType,
    GrainReferenceShared shared) : Orleans.GrainReferences.IGrainReferenceActivatorProvider
{
    public bool TryGet(
        GrainType grainType,
        GrainInterfaceType interfaceType,
        [NotNullWhen(true)] out Orleans.GrainReferences.IGrainReferenceActivator? activator)
    {
        activator = new TrimSmokeReferenceActivator(proxyType, shared);
        return true;
    }
}

internal sealed class TrimSmokeReferenceActivator(
    Type proxyType,
    GrainReferenceShared shared) : Orleans.GrainReferences.IGrainReferenceActivator
{
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2067",
        Justification = "TypeManifestOptions.AddInterfaceProxy preserves the public constructor used to instantiate generated proxy types.")]
    public GrainReference CreateReference(GrainId grainId) =>
        (GrainReference)Activator.CreateInstance(proxyType, shared, grainId.Key)!;
}

[Serializable]
internal sealed class SerializablePayload : ISerializable, IDeserializationCallback
{
    private static readonly List<string> CallbackHistory = [];

    public SerializablePayload(string payload, int revision)
    {
        Payload = payload;
        Revision = revision;
        ConstructorRestoredState = "not restored";
    }

    private SerializablePayload(SerializationInfo info, StreamingContext context)
    {
        CallbackHistory.Add("serialization_ctor");
        Payload = info.GetString(nameof(Payload))!;
        Revision = info.GetInt32(nameof(Revision));
        ConstructorRestoredState = $"{Payload}:{Revision}";
    }

    public static IReadOnlyList<string> History => CallbackHistory;

    public string Payload { get; private set; }

    public int Revision { get; private set; }

    public string ConstructorRestoredState { get; private set; }

    public static void ResetHistory() => CallbackHistory.Clear();

    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
    {
        CallbackHistory.Add("get_object_data");
        info.AddValue(nameof(Payload), Payload);
        info.AddValue(nameof(Revision), Revision);
    }

    [OnSerializing]
    private void OnSerializing(StreamingContext context) => CallbackHistory.Add("on_serializing");

    [OnSerialized]
    private void OnSerialized(StreamingContext context) => CallbackHistory.Add("on_serialized");

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context) => CallbackHistory.Add("on_deserializing");

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context) => CallbackHistory.Add("on_deserialized");

    void IDeserializationCallback.OnDeserialization(object? sender) => CallbackHistory.Add("on_deserialization");
}
