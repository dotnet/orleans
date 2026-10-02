using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.ContextSmoke;

public static partial class StaticFactoryContracts
{
    public static void KeyedDescriptorsDoNotShadowUnkeyedInstances()
    {
        var unkeyed = new KeyedDependency<int>();
        var keyed = new KeyedDependency<int>();
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.AddSingleton<IKeyedDependency<int>>(unkeyed);
        services.AddKeyedSingleton<IKeyedDependency<int>>("keyed", keyed);
        services.AddKeyedSingleton(typeof(IKeyedDependency<>), "open-keyed", typeof(KeyedDependency<>));
        services.Configure<TypeManifestOptions>(options =>
            options.AddSerializerService<KeyedLookupResult>(provider =>
            {
                var dependency = provider.Services.GetRequiredService<IKeyedDependency<int>>();
                var availability = (IServiceProviderIsService)provider.Services.GetRequiredService(typeof(IServiceProviderIsService));
                Ensure(availability.IsService(typeof(IKeyedDependency<int>)), "Unkeyed instance is available during graph construction.");
                Ensure(!availability.IsService(typeof(IKeyedDependency<string>)), "Keyed open generic is absent from unkeyed availability.");
                Ensure(provider.Services.GetService(typeof(IKeyedDependency<string>)) is null, "Keyed open generic is absent from unkeyed resolution.");
                return new KeyedLookupResult(dependency);
            }));
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var result = OrleansGeneratedCodeHelper.GetService<KeyedLookupResult>(null!, codecs);
        Ensure(ReferenceEquals(result.Dependency, unkeyed), "Later keyed descriptors preserve the exact unkeyed instance.");
        Ensure(ReferenceEquals(scope.GetRequiredKeyedService<IKeyedDependency<int>>("keyed"), keyed), "Keyed registration remains independently available.");
    }

    public static void KeyedOnlyDescriptorsDoNotSelectDependencyConstructors()
    {
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.AddKeyedSingleton<IKeyedDependency<int>>("exact-keyed", new KeyedDependency<int>());
        services.AddKeyedSingleton(typeof(IKeyedDependency<>), "open-keyed", typeof(KeyedDependency<>));
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<ExactKeyedConstructorProbe>(provider =>
                ActivatorUtilities.CreateInstance<ExactKeyedConstructorProbe>(provider.Services));
            options.AddSerializerService<OpenKeyedConstructorProbe>(provider =>
                ActivatorUtilities.CreateInstance<OpenKeyedConstructorProbe>(provider.Services));
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var exact = OrleansGeneratedCodeHelper.GetService<ExactKeyedConstructorProbe>(null!, codecs);
        var open = OrleansGeneratedCodeHelper.GetService<OpenKeyedConstructorProbe>(null!, codecs);
        Ensure(!exact.UsedDependency && !open.UsedDependency, "Keyed-only exact and open registrations select the parameterless constructors.");
    }

    public interface IKeyedDependency<T> { }
    public sealed class KeyedDependency<T> : IKeyedDependency<T> { }
    private sealed class KeyedLookupResult(IKeyedDependency<int> dependency)
    {
        public IKeyedDependency<int> Dependency { get; } = dependency;
    }

    public sealed class ExactKeyedConstructorProbe
    {
        public ExactKeyedConstructorProbe() { }
        public ExactKeyedConstructorProbe(IKeyedDependency<int> dependency) => UsedDependency = true;
        public bool UsedDependency { get; }
    }

    public sealed class OpenKeyedConstructorProbe
    {
        public OpenKeyedConstructorProbe() { }
        public OpenKeyedConstructorProbe(IKeyedDependency<string> dependency) => UsedDependency = true;
        public bool UsedDependency { get; }
    }

    public static void DirectProviderServicesRemainGraphOwned()
    {
        using var gate = new ConstructionGate();
        var services = new ServiceCollection().AddSerializerContext(new ConstructionContext());
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<ServiceProbe>(_ => new ServiceProbe(gate));
            options.AddSerializerService<IBaseCodec<FirstValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<IValueSerializer<ProbeValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<Orleans.Serialization.Cloning.IBaseCopier<FirstValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<Orleans.Serialization.Activators.IActivator<FirstValue>>(static provider => OrleansGeneratedCodeHelper.GetService<ServiceProbe>(null!, provider));
            options.AddSerializerService<DirectOwner>(provider => new DirectOwner(provider, gate));
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        Expect<InvalidOperationException>(() => OrleansGeneratedCodeHelper.GetService<DirectOwner>(null!, codecs), "direct owner failure");
        var root = OrleansGeneratedCodeHelper.GetService<DirectOwner>(null!, codecs);
        Ensure(root.Services.All(value => ReferenceEquals(value, root.Services[0])), "All direct base/value/copier/activator APIs resolve the same graph-owned implementation.");
        Ensure(ReferenceEquals(root.Services[0], codecs.GetActivator<FirstValue>()), "Direct service remains canonical after commit.");
        Ensure(gate.FirstConstructions == 2, "Rollback rebuilds services resolved through direct provider APIs.");
    }

#if !NATIVE_AOT_SMOKE
    public static void CaughtBaseCodecSpecializationFailureFaultsGraph()
    {
        var state = new BaseSpecializationState();
        var specializer = new FailingBaseSpecializer(state);
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton<ISpecializableBaseCodec>(specializer);
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<BaseSpecializationLeaf>(_ => new BaseSpecializationLeaf(state));
            options.AddSerializerService<BaseSpecializationRoot>(provider => new BaseSpecializationRoot(provider, state));
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        state.Provider = codecs;
        var committed = codecs.GetCodec<int>();
        Exception? failure = null;
        try
        {
            _ = OrleansGeneratedCodeHelper.GetService<BaseSpecializationRoot>(null!, codecs);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        Ensure(ReferenceEquals(state.Failure, failure) && ReferenceEquals(state.Failure, state.CaughtFailure),
            "Caught direct base-codec specialization failure aborts the graph with the original exception.");
        var root = OrleansGeneratedCodeHelper.GetService<BaseSpecializationRoot>(null!, codecs);
        var leaf = OrleansGeneratedCodeHelper.GetService<BaseSpecializationLeaf>(null!, codecs);
        var baseCodec = (SpecializedBaseCodec)codecs.GetBaseCodec<BaseSpecializationValue>();
        Ensure(ReferenceEquals(root.Leaf, leaf) && ReferenceEquals(baseCodec.Leaf, leaf),
            "Retry rebuilds canonical pending services and the specialized base codec.");
        Ensure(!ReferenceEquals(state.FailedLeaf, leaf), "Failed specialization publishes no pending leaf.");
        Ensure(ReferenceEquals(root, OrleansGeneratedCodeHelper.GetService<BaseSpecializationRoot>(null!, codecs)),
            "Successful retry commits one canonical root.");
        Ensure(ReferenceEquals(committed, codecs.GetCodec<int>()), "Rollback retains already committed codec leaves.");
        Ensure(state.RootConstructions == 2 && state.LeafConstructions == 2 && state.Specializations == 2,
            "Retry reconstructs the root, leaf, and specialization exactly once.");
    }

    private sealed class BaseSpecializationState
    {
        public CodecProvider Provider { get; set; } = null!;
        public InvalidOperationException Failure { get; } = new("base specialization failure");
        public Exception? CaughtFailure { get; set; }
        public BaseSpecializationLeaf? FailedLeaf { get; set; }
        public int RootConstructions;
        public int LeafConstructions;
        public int Specializations;
    }

    private sealed class BaseSpecializationLeaf
    {
        public BaseSpecializationLeaf(BaseSpecializationState state) => state.LeafConstructions++;
    }

    private sealed class BaseSpecializationRoot
    {
        public BaseSpecializationLeaf Leaf { get; }
        public BaseSpecializationRoot(ICodecProvider provider, BaseSpecializationState state)
        {
            state.RootConstructions++;
            Leaf = OrleansGeneratedCodeHelper.GetService<BaseSpecializationLeaf>(this, provider);
            try
            {
                _ = provider.GetBaseCodec<BaseSpecializationValue>();
            }
            catch (InvalidOperationException exception)
            {
                state.CaughtFailure = exception;
            }
        }
    }

    private sealed class BaseSpecializationValue { }

    private sealed class FailingBaseSpecializer(BaseSpecializationState state) : ISpecializableBaseCodec
    {
        public bool IsSupportedType(Type type) => type == typeof(BaseSpecializationValue);
        public IBaseCodec GetSpecializedCodec(Type type)
        {
            var leaf = OrleansGeneratedCodeHelper.GetService<BaseSpecializationLeaf>(this, state.Provider);
            if (++state.Specializations == 1)
            {
                state.FailedLeaf = leaf;
                throw state.Failure;
            }

            return new SpecializedBaseCodec(leaf);
        }
    }

    private sealed class SpecializedBaseCodec(BaseSpecializationLeaf leaf) : IBaseCodec<BaseSpecializationValue>
    {
        public BaseSpecializationLeaf Leaf { get; } = leaf;
        public void Serialize<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, BaseSpecializationValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> { }
        public void Deserialize<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, BaseSpecializationValue value) { }
    }

    public static void SingletonFirstLookupCannotInvertGraphLock()
    {
        using var singletonStarted = new ManualResetEventSlim();
        using var releaseSingleton = new ManualResetEventSlim();
        using var ownerStarted = new ManualResetEventSlim();
        var services = new ServiceCollection().AddSerializer();
        services.AddSingleton<SingletonProbe>(provider =>
        {
            singletonStarted.Set();
            Ensure(releaseSingleton.Wait(TimeSpan.FromSeconds(10)), "Singleton-first constructor released.");
            return new SingletonProbe(OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, provider.GetRequiredService<CodecProvider>()));
        });
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<DiLeaf>(static _ => new DiLeaf(new ConstructionGate()));
            options.AddSerializerService<SingletonOwner>(provider =>
            {
                ownerStarted.Set();
                return new SingletonOwner(provider);
            });
        });
        using var scope = services.BuildServiceProvider();
        var codecs = scope.GetRequiredService<CodecProvider>();
        var committed = OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, codecs);
        var singleton = Task.Run(() => scope.GetRequiredService<SingletonProbe>());
        Ensure(singletonStarted.Wait(TimeSpan.FromSeconds(10)), "DI singleton constructor entered before graph owner.");
        var owner = Task.Run(() => Expect<InvalidOperationException>(
            () => OrleansGeneratedCodeHelper.GetService<SingletonOwner>(null!, codecs), "cannot resolve"));
        try
        {
            Ensure(ownerStarted.Wait(TimeSpan.FromSeconds(10)), "Graph owner started.");
            owner.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally
        {
            releaseSingleton.Set();
        }
        var result = singleton.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Ensure(ReferenceEquals(committed, result.Leaf), "Singleton uses the committed provider service after rejection releases the graph.");
    }

    public static void CaughtAutomaticAndMissingFailuresFaultGraph()
    {
        foreach (var missing in new[] { false, true })
        {
            using var gate = new ConstructionGate();
            var services = new ServiceCollection().AddSerializer();
            services.AddSingleton(gate);
            services.Configure<TypeManifestOptions>(options =>
            {
                options.AddSerializerService<DiLeaf>(_ => new DiLeaf(gate));
                options.AddSerializerService<AutomaticCatcher>(provider => new AutomaticCatcher(provider, missing));
            });
            using var scope = services.BuildServiceProvider();
            var codecs = scope.GetRequiredService<CodecProvider>();
            Expect<Exception>(() => OrleansGeneratedCodeHelper.GetService<AutomaticCatcher>(null!, codecs),
                missing ? "Could not find" : "automatic constructor failure");
            var leaf = OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, codecs);
            Ensure(leaf is not null && gate.FirstConstructions == 2, "Caught automatic/missing failure discards the pending leaf.");
        }
    }

    public static void HolderCachesOnlyPublishedDependencies()
    {
        using var gate = new ConstructionGate { FailFirst = true };
        var services = new ServiceCollection().AddSerializer();
        IFieldCodec<DiLeaf>? holder = null;
        services.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializerService<DiLeaf>(_ => new DiLeaf(gate));
            options.AddSerializer<DiLeaf>(static provider => new LeafCodec(OrleansGeneratedCodeHelper.GetService<DiLeaf>(null!, provider)),
                static _ => new Orleans.Serialization.Cloning.ShallowCopier<DiLeaf>());
            options.AddSerializerService<HolderOwner>(provider => new HolderOwner(provider,
                holder ?? throw new InvalidOperationException("Holder must be resolved before owner construction."), gate));
        });
        using var scope = services.BuildServiceProvider();
        holder = scope.GetRequiredService<IFieldCodec<DiLeaf>>();
        var holderValue = holder.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Expected the singleton field codec holder's public value.");
        var codecs = scope.GetRequiredService<CodecProvider>();
        Expect<InvalidOperationException>(() => OrleansGeneratedCodeHelper.GetService<HolderOwner>(null!, codecs), "injected constructor failure");
        var owner = OrleansGeneratedCodeHelper.GetService<HolderOwner>(null!, codecs);
        var canonical = (LeafCodec)codecs.GetCodec<DiLeaf>();
        Ensure(ReferenceEquals(owner.Codec, canonical) && ReferenceEquals(holderValue.GetValue(holder), canonical), "Singleton holder re-resolves after the pending graph rolled back.");
        Ensure(gate.SecondConstructions == 2, "Holder owner retries its full construction.");
    }

    private sealed class SingletonProbe(DiLeaf leaf) { public DiLeaf Leaf { get; } = leaf; }
    private sealed class SingletonOwner
    {
        public SingletonOwner(ICodecProvider provider)
            => _ = OrleansGeneratedCodeHelper.GetService<SingletonProbe>(this, provider);
    }

    private sealed class AutomaticCatcher
    {
        public AutomaticCatcher(ICodecProvider provider, bool missing)
        {
            _ = OrleansGeneratedCodeHelper.GetService<DiLeaf>(this, provider);
            try
            {
                if (missing) _ = provider.GetCodec(typeof(UnregisteredValue));
                else _ = OrleansGeneratedCodeHelper.GetService<AutomaticFailure>(this, provider);
            }
            catch (Exception) { }
        }
    }

    private sealed class AutomaticFailure
    {
        public AutomaticFailure(ICodecProvider provider)
        {
            _ = OrleansGeneratedCodeHelper.GetService<DiLeaf>(this, provider);
            throw new InvalidOperationException("automatic constructor failure");
        }
    }
    private sealed class UnregisteredValue;

    private sealed class HolderOwner
    {
        public IFieldCodec<DiLeaf> Codec { get; }
        public HolderOwner(ICodecProvider provider, IFieldCodec<DiLeaf> holder, ConstructionGate gate)
        {
            _ = new ListCodec<DiLeaf>(holder);
            Codec = (IFieldCodec<DiLeaf>)holder.GetType().GetProperty("Value")!.GetValue(holder)!;
            Interlocked.Increment(ref gate.SecondConstructions);
            if (gate.SecondConstructions == 1) throw new InvalidOperationException("injected constructor failure");
        }
    }

    private sealed class LeafCodec(DiLeaf leaf) : IFieldCodec<DiLeaf>
    {
        public DiLeaf Leaf { get; } = leaf;
        public void WriteField<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta,
            [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] DiLeaf value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public DiLeaf ReadValue<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, Orleans.Serialization.WireProtocol.Field field)
            => throw new NotSupportedException();
    }
#endif

    private struct ProbeValue { }

    private sealed class DirectOwner
    {
        public object[] Services { get; }
        public DirectOwner(ICodecProvider provider, ConstructionGate gate)
        {
            Services = [provider.GetBaseCodec<FirstValue>(), provider.GetValueSerializer<ProbeValue>(),
                provider.GetBaseCopier<FirstValue>(), provider.GetActivator<FirstValue>()];
            if (Interlocked.Increment(ref gate.SecondConstructions) == 1)
                throw new InvalidOperationException("direct owner failure");
        }
    }

    private sealed class ServiceProbe : IBaseCodec<FirstValue>, IValueSerializer<ProbeValue>,
        Orleans.Serialization.Cloning.IBaseCopier<FirstValue>, Orleans.Serialization.Activators.IActivator<FirstValue>
    {
        public ServiceProbe(ConstructionGate gate) => Interlocked.Increment(ref gate.FirstConstructions);
        public FirstValue Create() => new();
        public void DeepCopy(FirstValue input, FirstValue output, Orleans.Serialization.Cloning.CopyContext context) { }
        public void Serialize<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, FirstValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public void Deserialize<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, FirstValue value) => throw new NotSupportedException();
        public void Serialize<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref ProbeValue value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();
        public void Deserialize<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref ProbeValue value) => throw new NotSupportedException();
    }
}
