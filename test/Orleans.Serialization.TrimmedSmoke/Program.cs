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
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This smoke registers DotNetSerializableCodec only for SerializablePayload, whose constructors and callback methods are explicitly rooted above.")]
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
        ValidateArrayFallbacks(serviceProvider);
        ValidateRecursiveArrayRoots();
        ValidateGeneratedSerializer(serviceProvider);
        ValidateManualRegistrations(codecProvider);
        ValidateSerializableCallbacks(serviceProvider);
        ValidateGeneratedHelper(codecProvider);
        ValidateConfigurationAnalyzer(serviceProvider, codecProvider);
        ValidateGeneratedProxy(serviceProvider, codecProvider);
    }

    private static void ValidateRecursiveArrayRoots()
    {
        foreach (var rankThreeRoot in new[] { false, true })
        {
            using var provider = new ServiceCollection().AddSerializer().BuildServiceProvider();
            var serializer = provider.GetRequiredService<Serializer>();
            var model = new RecursiveArrayPayload { Value = 42 };
            model.RankTwo = new[,] { { model, model } };
            model.RankThree = new[, ,] { { { model, model } } };
            model.RankTwoAlias = model.RankTwo;
            model.RankThreeAlias = model.RankThree;
            Array original;
            Array restored;
            if (rankThreeRoot)
            {
                var root = new[, ,] { { { model, model } } };
                original = root;
                restored = serializer.Deserialize<RecursiveArrayPayload[,,]>(serializer.SerializeToArray(root))!;
            }
            else
            {
                var root = new[,] { { model, model } };
                original = root;
                restored = serializer.Deserialize<RecursiveArrayPayload[,]>(serializer.SerializeToArray(root))!;
            }
            var copied = provider.GetRequiredService<DeepCopier>().Copy(original)!;
            var first = new int[original.Rank];
            var last = new int[original.Rank];
            last[^1] = 1;
            foreach (var result in new[] { restored, copied })
            {
                var value = (RecursiveArrayPayload)result.GetValue(first)!;
                Ensure(result.GetType() == original.GetType() && !ReferenceEquals(original, result),
                    "Recursive array roots retain their concrete shape and independent storage.");
                Ensure(value.Value == 42 && ReferenceEquals(value, result.GetValue(last))
                    && ReferenceEquals(value, value.RankTwo[0, 0]) && ReferenceEquals(value, value.RankThree[0, 0, 0]),
                    "Recursive array roots preserve repeated elements and cross-rank cycles.");
                Ensure(ReferenceEquals(value.RankTwo, value.RankTwoAlias)
                    && ReferenceEquals(value.RankThree, value.RankThreeAlias),
                    "Recursive array roots preserve array aliases.");
                Ensure(!ReferenceEquals(model, value) && !ReferenceEquals(model.RankTwo, value.RankTwo)
                    && !ReferenceEquals(model.RankThree, value.RankThree), "Recursive array roots isolate original members.");
            }
        }
    }

    private static void ValidateArrayFallbacks(IServiceProvider serviceProvider)
    {
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var copier = serviceProvider.GetRequiredService<DeepCopier>();
        foreach (var bounds in new[] { new[] { 0, 0 }, new[] { -2, 3 }, new[] { 4 } })
        {
            var original = Array.CreateInstance(typeof(string), Enumerable.Repeat(2, bounds.Length).ToArray(), bounds);
            original.SetValue("first", bounds);
            var last = bounds.Select(static bound => bound + 1).ToArray();
            original.SetValue("last", last);
            var copied = copier.Copy(original) ?? throw new InvalidOperationException("Array copying returned null.");
            var results = new List<Array> { copied };
            if (bounds.All(static bound => bound == 0))
            {
                results.Add(serializer.Deserialize<Array>(serializer.SerializeToArray<Array>(original))
                    ?? throw new InvalidOperationException("Array deserialization returned null."));
            }
            else
            {
                try
                {
                    _ = serializer.SerializeToArray<Array>(original);
                    throw new InvalidOperationException("Array serialization must diagnose non-zero lower bounds.");
                }
                catch (NotSupportedException exception)
                {
                    Ensure(exception.Message.Contains("non-zero lower bounds", StringComparison.Ordinal),
                        "Array serialization retains its lower-bound diagnostic.");
                }
            }
            foreach (var result in results)
            {
                Ensure(result.GetType() == original.GetType(), "Array fallback preserves the concrete array type.");
                Ensure(result.Rank == original.Rank, "Array fallback preserves array rank.");
                for (var dimension = 0; dimension < bounds.Length; dimension++)
                {
                    Ensure(result.GetLowerBound(dimension) == bounds[dimension], "Array fallback preserves lower bounds.");
                    Ensure(result.GetLength(dimension) == 2, "Array fallback preserves dimension lengths.");
                }
                Ensure(Equals(result.GetValue(bounds), "first") && Equals(result.GetValue(last), "last"),
                    "Array fallback preserves values.");
                Ensure(!ReferenceEquals(original, result), "Array serialization and copying create independent arrays.");
                result.SetValue("changed", bounds);
                Ensure(Equals(original.GetValue(bounds), "first"), "Array copy isolation preserves the original values.");
            }
        }
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
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This method only reads manifest types which were registered through trimming-aware TypeManifestOptions.Add* methods.")]
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

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This method only reads proxy types which were registered through TypeManifestOptions.AddInterfaceProxy.")]
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

[GenerateSerializer]
public sealed class RecursiveArrayPayload
{
    [Id(0)] public RecursiveArrayPayload[,] RankTwo { get; set; } = new RecursiveArrayPayload[0, 0];
    [Id(1)] public RecursiveArrayPayload[,,] RankThree { get; set; } = new RecursiveArrayPayload[0, 0, 0];
    [Id(2)] public RecursiveArrayPayload[,] RankTwoAlias { get; set; } = new RecursiveArrayPayload[0, 0];
    [Id(3)] public RecursiveArrayPayload[,,] RankThreeAlias { get; set; } = new RecursiveArrayPayload[0, 0, 0];
    [Id(4)] public int Value { get; set; }
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

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
    public CustomTarget<T>? DeepCopy(
        CustomTarget<T>? input,
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
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2077",
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
