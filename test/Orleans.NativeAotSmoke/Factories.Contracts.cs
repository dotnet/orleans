using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.ContextSmoke;

public static class StaticFactoryContracts
{
    public static void CaughtNestedFailureFaultsTheWholeGraph()
    {
        using var gate = new ConstructionGate { FailFirst = true };
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<CatchingRoot>(provider => new CatchingRoot(provider));
            options.AddSerializerService<FailingNode>(provider => new FailingNode(provider, gate));
            options.AddSerializerService<DependentNode>(provider => new DependentNode(provider, gate));
        });
        using var provider = services.BuildServiceProvider();
        var codecs = provider.GetRequiredService<CodecProvider>();
        Expect<InvalidOperationException>(() => OrleansGeneratedCodeHelper.GetService<CatchingRoot>(null!, codecs), "injected constructor failure");
        var root = OrleansGeneratedCodeHelper.GetService<CatchingRoot>(null!, codecs);
        Ensure(root.Child is not null && ReferenceEquals(root.Child, root.Child.Dependent.Owner), "Caught failure cannot commit a dependency linked to the failed node.");
        Ensure(gate.FirstConstructions == 2 && gate.SecondConstructions == 2, "Caught nested failure rebuilds the entire pending graph.");
    }

#if !NATIVE_AOT_SMOKE
    public static void DiSingletonsCannotRetainPendingServices()
    {
        using var gate = new ConstructionGate();
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton(gate);
        services.AddSingleton<DiBridge>();
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<DiLeaf>(provider => new DiLeaf(gate));
            options.AddSerializerService<DiOwner>(provider => new DiOwner(provider));
        });
        using var provider = services.BuildServiceProvider();
        var codecs = provider.GetRequiredService<CodecProvider>();
        Expect<InvalidOperationException>(() => OrleansGeneratedCodeHelper.GetService<DiOwner>(null!, codecs), "cannot cache an unpublished");
        var leaf = OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, codecs);
        var owner = OrleansGeneratedCodeHelper.GetService<DiOwner>(null!, codecs);
        Ensure(ReferenceEquals(leaf, owner.Leaf) && ReferenceEquals(leaf, owner.Bridge.Leaf), "DI singleton retains only a previously committed static service.");
        Ensure(gate.FirstConstructions == 2 && gate.SecondConstructions == 2, "Rejected DI construction neither caches its bridge nor its pending leaf.");
    }

    public static void SupplementalFactoriesPreserveAutomaticMetadata()
    {
        using var gate = new ConstructionGate();
        using var services = CreateMixedServices(gate, copier: false, cyclic: false);
        var provider = services.GetRequiredService<CodecProvider>();
        Ensure(services.GetRequiredService<TypeConverter>().Parse("System.Int32") == typeof(int), "Automatic type resolution remains available beside supplemental factories.");
        Ensure(provider.GetCodec<SecondValue>() is AutomaticLeafCodec, "Existing codec metadata remains available.");
        Ensure(provider.GetDeepCopier<SecondValue>() is AutomaticLeafCopier, "Existing copier metadata remains available.");
        Ensure(provider.GetCodec<FirstValue>() is AutomaticOuterCodec, "Acyclic closed factories can depend on automatic metadata.");
    }

    public static void AutomaticCacheEntriesRollBackWithFactoryFailures()
    {
        foreach (var copier in new[] { false, true })
        {
            using var gate = new ConstructionGate { FailFirst = true };
            using var services = CreateMixedServices(gate, copier, cyclic: false);
            var provider = services.GetRequiredService<CodecProvider>();
            _ = provider.GetCodec<int>();
            Expect<InvalidOperationException>(() => ResolveFirst(provider, copier), "injected constructor failure");
            _ = ResolveFirst(provider, copier);
            Ensure(gate.FirstConstructions == 2 && gate.SecondConstructions == 2, "Legacy cache entries produced by a failed factory transaction are rolled back.");
        }
    }

    public static void MixedConstructionCyclesFailBeforePublication()
    {
        foreach (var copier in new[] { false, true })
        {
            using var gate = new ConstructionGate();
            using var services = CreateMixedServices(gate, copier, cyclic: true);
            var provider = services.GetRequiredService<CodecProvider>();
            _ = provider.GetCodec<int>();
            Expect<InvalidOperationException>(() => ResolveFirst(provider, copier), "closed factories for every");
            Expect<InvalidOperationException>(() => ResolveFirst(provider, copier), "closed factories for every");
            Ensure(gate.SecondConstructions == 2, "Rejected mixed cycles leave no stale automatic cache entry.");
        }
    }

    private static ServiceProvider CreateMixedServices(ConstructionGate gate, bool copier, bool cyclic)
    {
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton(gate);
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddFieldCodec(cyclic ? typeof(SecondCodec) : typeof(AutomaticLeafCodec));
            options.AddCopier(cyclic ? typeof(SecondCopier) : typeof(AutomaticLeafCopier));
            if (cyclic)
            {
                options.AddSerializerService<FirstCodec>(provider => new FirstCodec(provider, gate));
                options.AddSerializerService<FirstCopier>(provider => new FirstCopier(provider, gate));
                options.AddSerializer<FirstValue>(
                    static provider => OrleansGeneratedCodeHelper.GetService<FirstCodec>(null!, provider),
                    static provider => OrleansGeneratedCodeHelper.GetService<FirstCopier>(null!, provider));
            }
            else
            {
                options.AddSerializerService<AutomaticOuterCodec>(provider => new AutomaticOuterCodec(provider, gate));
                options.AddSerializerService<AutomaticOuterCopier>(provider => new AutomaticOuterCopier(provider, gate));
                options.AddSerializer<FirstValue>(
                    static provider => OrleansGeneratedCodeHelper.GetService<AutomaticOuterCodec>(null!, provider),
                    static provider => OrleansGeneratedCodeHelper.GetService<AutomaticOuterCopier>(null!, provider));
            }
        });
        return services.BuildServiceProvider();
    }
#endif

    public static void CyclicConstructionPublishesCompletedGraphs()
    {
        foreach (var copier in new[] { false, true })
        {
            using var gate = new ConstructionGate { Pause = true };
            using var services = CreateConstructionServices(gate);
            var provider = services.GetRequiredService<CodecProvider>();
            var constructing = Task.Run(() => ResolveFirst(provider, copier));
            Ensure(gate.Paused.Wait(TimeSpan.FromSeconds(10)), "Outer constructor reached its dependency-ready barrier.");
            var consumer = Task.Run(() =>
            {
                gate.ConsumerStarted.Set();
                ExerciseSecond(services, copier);
            });

            try
            {
                Ensure(gate.ConsumerStarted.Wait(TimeSpan.FromSeconds(10)), "Concurrent consumer started resolution.");
                Ensure(!consumer.Wait(TimeSpan.FromMilliseconds(200)), "A pending cyclic dependency graph escaped before its outer constructor completed.");
            }
            finally
            {
                gate.Release.Set();
            }

            Task.WhenAll(constructing, consumer).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Ensure(gate.FirstConstructions == 1 && gate.SecondConstructions == 1, "Concurrent resolution shares one completed cyclic graph.");
        }
    }

    public static void FailedCyclicConstructionRollsBack()
    {
        foreach (var copier in new[] { false, true })
        {
            using var gate = new ConstructionGate { FailFirst = true };
            using var services = CreateConstructionServices(gate);
            var provider = services.GetRequiredService<CodecProvider>();
            var committedLeaf = provider.GetCodec<int>();
            var converter = services.GetRequiredService<TypeConverter>();
            Ensure(converter.Parse(converter.Format(typeof(SecondValue))) == typeof(SecondValue), "Explicit factory registration authorizes and resolves its closed type name.");
            Expect<InvalidOperationException>(() => ResolveFirst(provider, copier), "injected constructor failure");
            var first = ResolveFirst(provider, copier);
            object dependency = copier
                ? ((SecondCopier)provider.GetDeepCopier<SecondValue>()).First
                : ((SecondCodec)provider.GetCodec<SecondValue>()).First;
            Ensure(ReferenceEquals(first, dependency), "Retry replaces every pending dependency on the failed instance.");
            Ensure(gate.FirstConstructions == 2 && gate.SecondConstructions == 2, "Failed construction leaves no cached dependent service.");
            Ensure(ReferenceEquals(committedLeaf, provider.GetCodec<int>()), "Rollback preserves previously committed services.");
            ExerciseSecond(services, copier);
        }
    }

    private static ServiceProvider CreateConstructionServices(ConstructionGate gate)
    {
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<FirstCodec>(provider => new FirstCodec(provider, gate));
            options.AddSerializerService<SecondCodec>(provider => new SecondCodec(provider, gate));
            options.AddSerializerService<FirstCopier>(provider => new FirstCopier(provider, gate));
            options.AddSerializerService<SecondCopier>(provider => new SecondCopier(provider, gate));
            options.AddSerializer<FirstValue>(
                static provider => OrleansGeneratedCodeHelper.GetService<FirstCodec>(null!, provider),
                static provider => OrleansGeneratedCodeHelper.GetService<FirstCopier>(null!, provider));
            options.AddSerializer<SecondValue>(
                static provider => OrleansGeneratedCodeHelper.GetService<SecondCodec>(null!, provider),
                static provider => OrleansGeneratedCodeHelper.GetService<SecondCopier>(null!, provider));
        });
        return services.BuildServiceProvider();
    }

    private static object ResolveFirst(CodecProvider provider, bool copier)
        => copier ? provider.GetDeepCopier<FirstValue>() : provider.GetCodec<FirstValue>();

    private sealed class ConstructionContext : SerializerContext
    {
        protected override void ConfigureInner(TypeManifestOptions options)
        {
            options.AddSerializer<int>(static _ => new Int32Codec(), static _ => new ShallowCopier<int>());
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T exception)
        {
            Ensure(exception.Message.Contains(message, StringComparison.Ordinal), "Constructor failure retains its diagnostic.");
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void ExerciseSecond(IServiceProvider services, bool copier)
    {
        if (copier)
        {
            Ensure(services.GetRequiredService<DeepCopier>().Copy(new SecondValue { Value = 47 }).Value == 47, "Concurrent copier uses a fully initialized dependency.");
        }
        else
        {
            Ensure(services.GetRequiredService<Serializer>().SerializeToArray(new SecondValue { Value = 47 }).Length > 0, "Concurrent codec uses a fully initialized dependency.");
        }
    }

    private sealed class ConstructionGate : IDisposable
    {
        public ManualResetEventSlim Paused { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim ConsumerStarted { get; } = new();
        public bool Pause { get; init; }
        public bool FailFirst { get; init; }
        public int FirstConstructions;
        public int SecondConstructions;

        public void ConstructFirst()
        {
            var attempt = Interlocked.Increment(ref FirstConstructions);
            if (FailFirst && attempt == 1) throw new InvalidOperationException("injected constructor failure");
            if (Pause)
            {
                Paused.Set();
                Ensure(Release.Wait(TimeSpan.FromSeconds(10)), "Outer cyclic constructor was released.");
            }
        }

        public void Dispose()
        {
            Paused.Dispose();
            Release.Dispose();
            ConsumerStarted.Dispose();
        }
    }

    private sealed class FirstValue { public int Value { get; init; } }
    private sealed class SecondValue { public int Value { get; init; } }

    private sealed class CatchingRoot
    {
        public FailingNode? Child { get; }
        public CatchingRoot(ICodecProvider provider)
        {
            try { Child = OrleansGeneratedCodeHelper.GetService<FailingNode>(this, provider); }
            catch (InvalidOperationException) { }
        }
    }

    private sealed class FailingNode
    {
        public DependentNode Dependent { get; }
        public FailingNode(ICodecProvider provider, ConstructionGate gate)
        {
            Dependent = OrleansGeneratedCodeHelper.GetService<DependentNode>(this, provider);
            gate.ConstructFirst();
        }
    }

    private sealed class DependentNode
    {
        public FailingNode Owner { get; }
        public DependentNode(ICodecProvider provider, ConstructionGate gate)
        {
            Interlocked.Increment(ref gate.SecondConstructions);
            Owner = OrleansGeneratedCodeHelper.GetService<FailingNode>(this, provider);
        }
    }

#if !NATIVE_AOT_SMOKE
    private sealed class DiLeaf
    {
        public DiLeaf(ConstructionGate gate) => Interlocked.Increment(ref gate.FirstConstructions);
    }

    private sealed class DiOwner
    {
        public DiLeaf Leaf { get; }
        public DiBridge Bridge { get; }
        public DiOwner(ICodecProvider provider)
        {
            Leaf = OrleansGeneratedCodeHelper.GetService<DiLeaf>(this, provider);
            Bridge = OrleansGeneratedCodeHelper.GetService<DiBridge>(this, provider);
        }
    }

    private sealed class DiBridge
    {
        public DiLeaf Leaf { get; }
        public DiBridge(ICodecProvider provider, ConstructionGate gate)
        {
            Interlocked.Increment(ref gate.SecondConstructions);
            Leaf = OrleansGeneratedCodeHelper.GetService<DiLeaf>(this, provider);
        }
    }

    private class AutomaticLeafCodec : IFieldCodec<SecondValue>
    {
        public AutomaticLeafCodec(ConstructionGate gate) => Interlocked.Increment(ref gate.SecondConstructions);
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] SecondValue value)
            where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
        public SecondValue ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    }

    private class AutomaticLeafCopier : IDeepCopier<SecondValue>
    {
        public AutomaticLeafCopier(ConstructionGate gate) => Interlocked.Increment(ref gate.SecondConstructions);
        [return: NotNullIfNotNull(nameof(input))]
        public SecondValue? DeepCopy(SecondValue? input, CopyContext context) => input;
    }

    private sealed class AutomaticOuterCodec : IFieldCodec<FirstValue>
    {
        private readonly IFieldCodec<SecondValue> _leaf;
        public AutomaticOuterCodec(ICodecProvider provider, ConstructionGate gate)
        {
            _leaf = provider.GetCodec<SecondValue>();
            gate.ConstructFirst();
        }
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] FirstValue value)
            where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
        public FirstValue ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    }

    private sealed class AutomaticOuterCopier : IDeepCopier<FirstValue>
    {
        private readonly IDeepCopier<SecondValue> _leaf;
        public AutomaticOuterCopier(ICodecProvider provider, ConstructionGate gate)
        {
            _leaf = provider.GetDeepCopier<SecondValue>();
            gate.ConstructFirst();
        }
        [return: NotNullIfNotNull(nameof(input))]
        public FirstValue? DeepCopy(FirstValue? input, CopyContext context) => input;
    }
#endif

    private sealed class FirstCodec : IFieldCodec<FirstValue>
    {
        private readonly SecondCodec _second;
        private readonly bool _ready;

        public FirstCodec(ICodecProvider provider, ConstructionGate gate)
        {
            _second = OrleansGeneratedCodeHelper.GetService<SecondCodec>(this, provider);
            gate.ConstructFirst();
            _ready = true;
        }

        public void VerifyReady() => Ensure(_ready && ReferenceEquals(_second.First, this), "Codec dependency graph is complete.");

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] FirstValue value)
            where TBufferWriter : IBufferWriter<byte>
        {
            VerifyReady();
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }
            Int32Codec.WriteField(ref writer, fieldIdDelta, expectedType, value.Value, typeof(FirstValue));
        }

        public FirstValue ReadValue<TInput>(ref Reader<TInput> reader, Field field) => new() { Value = Int32Codec.ReadValue(ref reader, field) };
    }

    private sealed class SecondCodec : IFieldCodec<SecondValue>
    {
        public FirstCodec First { get; }

        public SecondCodec(ICodecProvider provider, ConstructionGate gate)
        {
            Interlocked.Increment(ref gate.SecondConstructions);
            First = OrleansGeneratedCodeHelper.GetService<FirstCodec>(this, provider);
        }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] SecondValue value)
            where TBufferWriter : IBufferWriter<byte>
        {
            First.VerifyReady();
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }
            Int32Codec.WriteField(ref writer, fieldIdDelta, expectedType, value.Value, typeof(SecondValue));
        }

        public SecondValue ReadValue<TInput>(ref Reader<TInput> reader, Field field) => new() { Value = Int32Codec.ReadValue(ref reader, field) };
    }

    private sealed class FirstCopier : IDeepCopier<FirstValue>
    {
        private readonly SecondCopier _second;
        private readonly bool _ready;

        public FirstCopier(ICodecProvider provider, ConstructionGate gate)
        {
            _second = OrleansGeneratedCodeHelper.GetService<SecondCopier>(this, provider);
            gate.ConstructFirst();
            _ready = true;
        }

        public void VerifyReady() => Ensure(_ready && ReferenceEquals(_second.First, this), "Copier dependency graph is complete.");

        [return: NotNullIfNotNull(nameof(input))]
        public FirstValue? DeepCopy(FirstValue? input, CopyContext context)
        {
            VerifyReady();
            return input is null ? null : new() { Value = input.Value };
        }
    }

    private sealed class SecondCopier : IDeepCopier<SecondValue>
    {
        public FirstCopier First { get; }

        public SecondCopier(ICodecProvider provider, ConstructionGate gate)
        {
            Interlocked.Increment(ref gate.SecondConstructions);
            First = OrleansGeneratedCodeHelper.GetService<FirstCopier>(this, provider);
        }

        [return: NotNullIfNotNull(nameof(input))]
        public SecondValue? DeepCopy(SecondValue? input, CopyContext context)
        {
            First.VerifyReady();
            return input is null ? null : new() { Value = input.Value };
        }
    }
}
