using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ProxyCopierResolutionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmRegisteredDependencyResolutionDoesNotAllocate(bool closedFactories)
    {
        var constructions = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.AddTransient<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services =>
        {
            constructions++;
            return ActivatorUtilities.CreateInstance<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services);
        });
        if (closedFactories)
        {
            registrations.Configure<TypeManifestOptions>(options =>
                options.AddSerializerService<PayloadActivator>(static _ => new()));
        }

        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var caller = new object();
        var expected = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(caller, provider);
        for (var i = 0; i < 100; i++)
        {
            _ = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(caller, provider);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(caller, provider);
        }

        var cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1, constructions);
        Assert.Same(expected, provider.GetDeepCopier<CacheResolutionPayload>());
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = ActivatorUtilities.GetServiceOrCreateInstance<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(provider.Services);
        }

        var activationBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1000 warm resolutions: cached={cachedBytes} bytes and 0 constructors; uncached activation={activationBytes} bytes and {constructions - 1} constructors; closed factories={closedFactories}");
        Assert.Equal(0, cachedBytes);
        Assert.True(activationBytes > 0);
        Assert.Equal(1001, constructions);
    }

    [Fact]
    public void WarmProxiesReuseProviderCopiersWithoutConstructingDependencies()
    {
        var constructions = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.AddTransient<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services =>
        {
            constructions++;
            return ActivatorUtilities.CreateInstance<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services);
        });
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var copier = provider.GetDeepCopier<CacheResolutionPayload>();
        var pool = services.GetRequiredService<CopyContextPool>();
        var proxy = CreateProxy(pool, provider);
        Assert.Same(copier, GetCopier<CacheResolutionPayload>(proxy));
        for (var i = 0; i < 1000; i++)
        {
            Assert.Same(copier, GetCopier<CacheResolutionPayload>(CreateProxy(pool, provider)));
        }

        Assert.Equal(1, constructions);
        AssertCopiedArguments(proxy, (value, list, array) => ((ICacheResolutionProxy)proxy).Send(value, list, array));
    }

    [Fact]
    public void ClosedGenericProxiesReuseCopiersAndPreserveReferenceIdentity()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        var copier = provider.GetDeepCopier<CacheResolutionPayload>();
        var proxy = new OrleansCodeGen.Orleans.Serialization.UnitTests.Proxy_IGenericCacheResolutionProxy<CacheResolutionPayload>(pool, provider);
        var second = new OrleansCodeGen.Orleans.Serialization.UnitTests.Proxy_IGenericCacheResolutionProxy<CacheResolutionPayload>(pool, provider);
        Assert.Same(copier, GetCopier<CacheResolutionPayload>(proxy));
        Assert.Same(copier, GetCopier<CacheResolutionPayload>(second));
        Assert.Same(provider.GetDeepCopier<List<CacheResolutionPayload>>(), GetCopier<List<CacheResolutionPayload>>(proxy));
        Assert.Same(provider.GetDeepCopier<CacheResolutionPayload[]>(), GetCopier<CacheResolutionPayload[]>(proxy));
        AssertCopiedArguments(proxy, (value, list, array) => ((IGenericCacheResolutionProxy<CacheResolutionPayload>)proxy).Send(value, list, array));

        var primitiveProxy = new OrleansCodeGen.Orleans.Serialization.UnitTests.Proxy_IGenericCacheResolutionProxy<int>(pool, provider);
        IInvokable? request = null;
        primitiveProxy.OnInvoke = value => request = value;
        _ = ((IGenericCacheResolutionProxy<int>)primitiveProxy).Send(42, [42], [42]);
        Assert.Equal(42, request!.GetArgument(0));
        Assert.Equal([42], Assert.IsType<List<int>>(request.GetArgument(1)));
        Assert.Equal([42], Assert.IsType<int[]>(request.GetArgument(2)));
        Assert.Same(provider.GetDeepCopier<int>(), GetCopier<int>(primitiveProxy));
    }

    [Fact]
    public void ConcreteCopiersAreCachedPerProvider()
    {
        var firstConstructions = 0;
        var secondConstructions = 0;
        var firstRegistrations = new ServiceCollection().AddSerializer();
        firstRegistrations.AddTransient<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services =>
        {
            firstConstructions++;
            return ActivatorUtilities.CreateInstance<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services);
        });
        var secondRegistrations = new ServiceCollection().AddSerializer();
        secondRegistrations.AddTransient<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services =>
        {
            secondConstructions++;
            return ActivatorUtilities.CreateInstance<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services);
        });
        using var first = firstRegistrations.BuildServiceProvider();
        using var second = secondRegistrations.BuildServiceProvider();
        var firstProvider = first.GetRequiredService<CodecProvider>();
        var secondProvider = second.GetRequiredService<CodecProvider>();
        var firstPool = first.GetRequiredService<CopyContextPool>();
        var secondPool = second.GetRequiredService<CopyContextPool>();
        var firstCopier = GetCopier<CacheResolutionPayload>(CreateProxy(firstPool, firstProvider));
        var secondCopier = GetCopier<CacheResolutionPayload>(CreateProxy(secondPool, secondProvider));
        Assert.IsType<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(firstCopier);
        Assert.IsType<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(secondCopier);
        Assert.NotSame(firstCopier, secondCopier);
        for (var i = 0; i < 100; i++)
        {
            Assert.Same(firstCopier, GetCopier<CacheResolutionPayload>(CreateProxy(firstPool, firstProvider)));
            Assert.Same(secondCopier, GetCopier<CacheResolutionPayload>(CreateProxy(secondPool, secondProvider)));
        }

        Assert.Same(firstCopier, firstProvider.GetDeepCopier<CacheResolutionPayload>());
        Assert.Same(secondCopier, secondProvider.GetDeepCopier<CacheResolutionPayload>());
        Assert.Equal(1, firstConstructions);
        Assert.Equal(1, secondConstructions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredSerializationImplementationsReuseProviderInstances(bool helperFirst)
    {
        AssertImplementationCached<OrleansCodeGen.Orleans.Serialization.UnitTests.Codec_CacheResolutionPayload>(
            static _ => { }, static provider => provider.GetCodec<CacheResolutionPayload>(), helperFirst);
        AssertImplementationCached<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(
            static _ => { }, static provider => provider.GetDeepCopier<CacheResolutionPayload>(), helperFirst);
        AssertImplementationCached<ListCodec<int>>(
            static _ => { }, static provider => provider.GetCodec<List<int>>(), helperFirst);
        AssertImplementationCached<ListCodec<string>>(
            static _ => { }, static provider => provider.GetCodec<List<string>>(), helperFirst);
        AssertImplementationCached<ListCopier<CacheResolutionPayload>>(
            static _ => { }, static provider => provider.GetDeepCopier<List<CacheResolutionPayload>>(), helperFirst);
        AssertImplementationCached<ArrayCodec<int>>(
            static _ => { }, static provider => provider.GetCodec<int[]>(), helperFirst);
        AssertImplementationCached<ArrayCopier<CacheResolutionPayload>>(
            static _ => { }, static provider => provider.GetDeepCopier<CacheResolutionPayload[]>(), helperFirst);
        AssertImplementationCached<PayloadActivator>(
            static options => options.AddActivator(typeof(PayloadActivator)),
            static provider => provider.GetActivator<CacheResolutionPayload>(), helperFirst);
    }

    private static void AssertImplementationCached<TService>(
        Action<TypeManifestOptions> configure,
        Func<CodecProvider, object> getImplementation,
        bool helperFirst) where TService : class
    {
        var constructions = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure(configure);
        registrations.AddTransient<TService>(services =>
        {
            constructions++;
            return ActivatorUtilities.CreateInstance<TService>(services);
        });
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var caller = new object();
        var expected = helperFirst
            ? OrleansGeneratedCodeHelper.GetService<TService>(caller, provider)
            : Assert.IsType<TService>(getImplementation(provider));
        for (var i = 0; i < 100; i++)
        {
            Assert.Same(expected, OrleansGeneratedCodeHelper.GetService<TService>(caller, provider));
            Assert.Same(expected, getImplementation(provider));
        }

        Assert.Equal(1, constructions);
    }

    [Fact]
    public void ConcreteAndInterfaceDependenciesKeepTheirResolutionSemantics()
    {
        var firstCalls = 0;
        var secondCalls = 0;
        var listCalls = 0;
        var firstCopier = new OverrideCopier();
        var secondCopier = new OverrideCopier();
        var listCopier = new ShallowCopier<List<CacheResolutionPayload>>();
        var firstRegistrations = new ServiceCollection().AddSerializer();
        firstRegistrations.Configure<TypeManifestOptions>(options =>
        {
            options.AddSerializer<CacheResolutionPayload>(_ => throw new NotSupportedException(), _ => { firstCalls++; return firstCopier; });
            options.AddSerializer<List<CacheResolutionPayload>>(_ => throw new NotSupportedException(), _ => { listCalls++; return listCopier; });
        });
        var secondRegistrations = new ServiceCollection().AddSerializer();
        secondRegistrations.Configure<TypeManifestOptions>(options =>
            options.AddSerializer<CacheResolutionPayload>(_ => throw new NotSupportedException(), _ => { secondCalls++; return secondCopier; }));
        using var first = firstRegistrations.BuildServiceProvider();
        using var second = secondRegistrations.BuildServiceProvider();
        for (var i = 0; i < 100; i++)
        {
            var proxy = CreateGenericProxy(first.GetRequiredService<CopyContextPool>(), first.GetRequiredService<CodecProvider>());
            Assert.Same(firstCopier, GetCopier<CacheResolutionPayload>(proxy));
            Assert.IsType<Orleans.Serialization.Codecs.ListCopier<CacheResolutionPayload>>(GetCopier<List<CacheResolutionPayload>>(proxy));
            Assert.Same(secondCopier, GetCopier<CacheResolutionPayload>(CreateGenericProxy(second.GetRequiredService<CopyContextPool>(), second.GetRequiredService<CodecProvider>())));
        }

        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        Assert.Equal(0, listCalls);
        var firstProxy = new OrleansCodeGen.Orleans.Serialization.UnitTests.Proxy_IGenericCacheResolutionProxy<CacheResolutionPayload>(
            first.GetRequiredService<CopyContextPool>(), first.GetRequiredService<CodecProvider>());
        Assert.Same(firstCopier, GetCopier<CacheResolutionPayload>(firstProxy));
        Assert.IsType<Orleans.Serialization.Codecs.ListCopier<CacheResolutionPayload>>(GetCopier<List<CacheResolutionPayload>>(firstProxy));
        IInvokable? request = null;
        firstProxy.OnInvoke = value => request = value;
        var input = new CacheResolutionPayload { Value = 42 };
        var list = new List<CacheResolutionPayload> { input };
        _ = ((IGenericCacheResolutionProxy<CacheResolutionPayload>)firstProxy).Send(input, list, []);
        Assert.Equal(43, Assert.IsType<CacheResolutionPayload>(request!.GetArgument(0)).Value);
        var copiedList = Assert.IsType<List<CacheResolutionPayload>>(request.GetArgument(1));
        Assert.NotSame(list, copiedList);
        Assert.Equal(43, Assert.Single(copiedList).Value);
        Assert.Equal(1, firstCalls);
        Assert.Equal(0, listCalls);

        var concreteProxy = CreateProxy(first.GetRequiredService<CopyContextPool>(), first.GetRequiredService<CodecProvider>());
        Assert.IsType<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(GetCopier<CacheResolutionPayload>(concreteProxy));
        request = null;
        concreteProxy.OnInvoke = value => request = value;
        _ = ((ICacheResolutionProxy)concreteProxy).Send(input, list, []);
        Assert.Equal(42, Assert.IsType<CacheResolutionPayload>(request!.GetArgument(0)).Value);
    }

    [Fact]
    public void FailedFactorySurfacesErrorAndProxyConstructionCanRetry()
    {
        var calls = 0;
        var copier = new OverrideCopier();
        IDeepCopier<List<int>>? failedDependency = null;
        IDeepCopier<List<int>>? completedDependency = null;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure<TypeManifestOptions>(options => options.AddSerializer<CacheResolutionPayload>(
            _ => throw new NotSupportedException(), provider =>
            {
                var dependency = provider.GetDeepCopier<List<int>>();
                if (++calls == 1)
                {
                    failedDependency = dependency;
                    throw new InvalidOperationException("Injected copier construction failure");
                }

                completedDependency = dependency;
                return copier;
            }));
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        var error = Assert.Throws<InvalidOperationException>(() => CreateGenericProxy(pool, provider));
        Assert.Equal("Injected copier construction failure", error.Message);
        Assert.Same(copier, GetCopier<CacheResolutionPayload>(CreateGenericProxy(pool, provider)));
        Assert.Same(copier, GetCopier<CacheResolutionPayload>(CreateGenericProxy(pool, provider)));
        Assert.Equal(2, calls);
        Assert.NotNull(failedDependency);
        Assert.NotNull(completedDependency);
        Assert.NotSame(failedDependency, completedDependency);
        Assert.Same(completedDependency, provider.GetDeepCopier<List<int>>());
    }

    [Fact]
    public void FailedConcreteCopierConstructionCanRetry()
    {
        var calls = 0;
        var failure = new InvalidOperationException("Injected concrete copier construction failure");
        var registrations = new ServiceCollection().AddSerializer();
        registrations.AddTransient<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services =>
        {
            if (++calls == 1)
            {
                throw failure;
            }

            return ActivatorUtilities.CreateInstance<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(services);
        });
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => CreateProxy(pool, provider)));
        var proxy = CreateProxy(pool, provider);
        Assert.Same(GetCopier<CacheResolutionPayload>(proxy), GetCopier<CacheResolutionPayload>(CreateProxy(pool, provider)));
        Assert.Equal(2, calls);
        AssertCopiedArguments(proxy, (value, list, array) => ((ICacheResolutionProxy)proxy).Send(value, list, array));
    }

    [Fact]
    public async Task ConcurrentProxiesPublishTheSameConcreteCopier()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<MyInvokableProxyBase>[32];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await start.Task;
                return CreateProxy(pool, provider);
            }, TestContext.Current.CancellationToken);
        }

        start.SetResult();
        var proxies = await Task.WhenAll(tasks);
        var expected = GetCopier<CacheResolutionPayload>(proxies[0]);
        Assert.IsType<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(expected);
        Assert.All(proxies, proxy => Assert.Same(expected, GetCopier<CacheResolutionPayload>(proxy)));
        Assert.Same(expected, provider.GetDeepCopier<CacheResolutionPayload>());
    }

    [Fact]
    public async Task ConcurrentProxiesUseOneCompletedFactoryInstance()
    {
        var calls = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure<TypeManifestOptions>(options => options.AddSerializer<CacheResolutionPayload>(
            _ => throw new NotSupportedException(), _ =>
            {
                Interlocked.Increment(ref calls);
                return new OverrideCopier();
            }));
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<MyInvokableProxyBase>[32];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await start.Task;
                return CreateGenericProxy(pool, provider);
            }, TestContext.Current.CancellationToken);
        }

        start.SetResult();
        var proxies = await Task.WhenAll(tasks);
        var expected = GetCopier<CacheResolutionPayload>(proxies[0]);
        Assert.All(proxies, proxy => Assert.Same(expected, GetCopier<CacheResolutionPayload>(proxy)));
        Assert.Equal(1, calls);
        Assert.Same(expected, provider.GetDeepCopier<CacheResolutionPayload>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArbitraryHelperServicesKeepTheirDiLifetimes(bool closedFactories)
    {
        var registrations = new ServiceCollection().AddSerializer();
        registrations.AddTransient<TransientDependency>();
        registrations.AddScoped<ScopedDependency>();
        using var services = registrations.BuildServiceProvider();
        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var options = new TypeManifestOptions();
        if (closedFactories)
        {
            options.AddSerializerService<PayloadActivator>(static _ => new());
        }

        var first = new CodecProvider(firstScope.ServiceProvider, Microsoft.Extensions.Options.Options.Create(options));
        var second = new CodecProvider(secondScope.ServiceProvider, Microsoft.Extensions.Options.Options.Create(options));
        var caller = new object();
        Assert.NotSame(OrleansGeneratedCodeHelper.GetService<TransientDependency>(caller, first), OrleansGeneratedCodeHelper.GetService<TransientDependency>(caller, first));
        var scoped = OrleansGeneratedCodeHelper.GetService<ScopedDependency>(caller, first);
        Assert.Same(scoped, OrleansGeneratedCodeHelper.GetService<ScopedDependency>(caller, first));
        Assert.NotSame(scoped, OrleansGeneratedCodeHelper.GetService<ScopedDependency>(caller, second));
    }

    private static void AssertCopiedArguments(
        MyInvokableProxyBase proxy,
        Func<CacheResolutionPayload, List<CacheResolutionPayload>, CacheResolutionPayload[], ValueTask> send)
    {
        var input = new CacheResolutionPayload { Value = 42 };
        input.Next = input;
        input.Children = [input];
        IInvokable? request = null;
        proxy.OnInvoke = value => request = value;
        _ = send(input, [input], [input]);
        var copy = Assert.IsType<CacheResolutionPayload>(request!.GetArgument(0));
        Assert.NotSame(input, copy);
        Assert.Equal(42, copy.Value);
        Assert.Same(copy, copy.Next);
        Assert.Same(copy, copy.Children[0]);
        Assert.Same(copy, Assert.IsType<List<CacheResolutionPayload>>(request.GetArgument(1))[0]);
        Assert.Same(copy, Assert.IsType<CacheResolutionPayload[]>(request.GetArgument(2))[0]);
    }

    private static MyInvokableProxyBase CreateProxy(CopyContextPool pool, CodecProvider provider) =>
        new OrleansCodeGen.Orleans.Serialization.UnitTests.Proxy_ICacheResolutionProxy(pool, provider);

    private static MyInvokableProxyBase CreateGenericProxy(CopyContextPool pool, CodecProvider provider) =>
        new OrleansCodeGen.Orleans.Serialization.UnitTests.Proxy_IGenericCacheResolutionProxy<CacheResolutionPayload>(pool, provider);

    private static object GetCopier<T>(MyInvokableProxyBase proxy) =>
        Assert.Single(proxy.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance), field => typeof(IDeepCopier<T>).IsAssignableFrom(field.FieldType)).GetValue(proxy)!;

    public sealed class TransientDependency { }
    public sealed class ScopedDependency { }

    public sealed class PayloadActivator : IActivator<CacheResolutionPayload>
    {
        public CacheResolutionPayload Create() => new();
    }

    private sealed class OverrideCopier : IDeepCopier<CacheResolutionPayload>
    {
        [return: NotNullIfNotNull(nameof(input))]
        public CacheResolutionPayload? DeepCopy(CacheResolutionPayload? input, CopyContext context) => input is null ? null : new() { Value = input.Value + 1 };
    }
}

[GenerateSerializer]
public sealed class CacheResolutionPayload
{
    [Id(0)] public int Value { get; set; }
    [Id(1)] public CacheResolutionPayload? Next { get; set; }
    [Id(2)] public List<CacheResolutionPayload> Children { get; set; } = [];
}

public interface ICacheResolutionProxy : IMyInvokableBaseType
{
    ValueTask Send(CacheResolutionPayload value, List<CacheResolutionPayload> list, CacheResolutionPayload[] array);
}

public interface IGenericCacheResolutionProxy<T> : IMyInvokableBaseType
{
    ValueTask Send(T value, List<T> list, T[] array);
}
