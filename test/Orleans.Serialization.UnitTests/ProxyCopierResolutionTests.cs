using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ProxyCopierResolutionTests(ITestOutputHelper output)
{
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
        Assert.Same(copier, GetPayloadCopier(proxy));
        var before = constructions;
        for (var i = 0; i < 1000; i++) Assert.Same(copier, GetPayloadCopier(CreateProxy(pool, provider)));
        output.WriteLine($"1000 warm proxy constructions: payload copier constructors={constructions - before}; total={constructions}");
        Assert.Equal(1, constructions);
        Assert.Equal(1, before);

        var input = new CacheResolutionPayload { Value = 42 };
        input.Next = input;
        input.Children = [input];
        IInvokable? request = null;
        proxy.OnInvoke = value => request = value;
        _ = ((ICacheResolutionProxy)proxy).Send(input, [input], [input]);
        var copy = Assert.IsType<CacheResolutionPayload>(request!.GetArgument(0));
        Assert.NotSame(input, copy);
        Assert.Equal(42, copy.Value);
        Assert.Same(copy, copy.Next);
        Assert.Same(copy, copy.Children[0]);
        Assert.Same(copy, Assert.IsType<List<CacheResolutionPayload>>(request.GetArgument(1))[0]);
        Assert.Same(copy, Assert.IsType<CacheResolutionPayload[]>(request.GetArgument(2))[0]);

        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _ = provider.GetDeepCopier<CacheResolutionPayload>();
        var providerBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        var caller = new object();
        var baselineConstructions = constructions;
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            _ = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.Copier_CacheResolutionPayload>(caller, provider);
        var helperBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        output.WriteLine($"1000 dependency resolutions: provider={providerBytes} bytes, 0 constructors; concrete helper={helperBytes} bytes, {constructions - baselineConstructions} constructors");
        Assert.Equal(0, providerBytes);
        Assert.Equal(1000, constructions - baselineConstructions);
        Assert.True(helperBytes > 0);
    }

    [Fact]
    public void ClosedFactoriesOverrideGeneratedAndCollectionCopiersPerProvider()
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
            var proxy = CreateProxy(first.GetRequiredService<CopyContextPool>(), first.GetRequiredService<CodecProvider>());
            Assert.Same(firstCopier, GetPayloadCopier(proxy));
            Assert.Contains(proxy.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance), field => ReferenceEquals(field.GetValue(proxy), listCopier));
            Assert.Same(secondCopier, GetPayloadCopier(CreateProxy(second.GetRequiredService<CopyContextPool>(), second.GetRequiredService<CodecProvider>())));
        }
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        Assert.Equal(1, listCalls);
        var firstProxy = CreateProxy(first.GetRequiredService<CopyContextPool>(), first.GetRequiredService<CodecProvider>());
        IInvokable? request = null;
        firstProxy.OnInvoke = value => request = value;
        var input = new CacheResolutionPayload { Value = 42 };
        var list = new List<CacheResolutionPayload> { input };
        _ = ((ICacheResolutionProxy)firstProxy).Send(input, list, []);
        Assert.Equal(43, Assert.IsType<CacheResolutionPayload>(request!.GetArgument(0)).Value);
        Assert.Same(list, request.GetArgument(1));
    }

    [Fact]
    public void FailedFactoryDoesNotPublishProxyDependenciesAndCanRetry()
    {
        var calls = 0;
        IDeepCopier<List<int>>? failedDependency = null;
        IDeepCopier<List<int>>? completedDependency = null;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure<TypeManifestOptions>(options => options.AddSerializer<CacheResolutionPayload>(
            _ => throw new NotSupportedException(), provider =>
            {
                calls++;
                // This dependency must be discarded if the containing construction graph fails.
                var dependency = provider.GetDeepCopier<List<int>>();
                if (calls == 1)
                {
                    failedDependency = dependency;
                    throw new InvalidOperationException("Injected copier construction failure");
                }
                completedDependency = dependency;
                return new OverrideCopier();
            }));
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        Assert.Throws<InvalidOperationException>(() => CreateProxy(pool, provider));
        var proxy = CreateProxy(pool, provider);
        Assert.Same(GetPayloadCopier(proxy), GetPayloadCopier(CreateProxy(pool, provider)));
        Assert.Equal(2, calls);
        Assert.NotNull(failedDependency);
        Assert.NotNull(completedDependency);
        Assert.NotSame(failedDependency, completedDependency);
        Assert.Same(completedDependency, provider.GetDeepCopier<List<int>>());
    }

    [Fact]
    public async Task ConcurrentProxiesUseOneCompletedFactoryInstance()
    {
        var calls = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure<TypeManifestOptions>(options => options.AddSerializer<CacheResolutionPayload>(
            _ => throw new NotSupportedException(), _ => { Interlocked.Increment(ref calls); return new OverrideCopier(); }));
        using var services = registrations.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var pool = services.GetRequiredService<CopyContextPool>();
        var tasks = new Task<MyInvokableProxyBase>[32];
        for (var i = 0; i < tasks.Length; i++) tasks[i] = Task.Run(() => CreateProxy(pool, provider));
        var proxies = await Task.WhenAll(tasks);
        Assert.All(proxies, proxy => Assert.Same(GetPayloadCopier(proxies[0]), GetPayloadCopier(proxy)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ArbitraryHelperServicesKeepTheirDiLifetimes()
    {
        var registrations = new ServiceCollection().AddSerializer();
        registrations.AddTransient<TransientDependency>();
        registrations.AddScoped<ScopedDependency>();
        using var services = registrations.BuildServiceProvider();
        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var first = new CodecProvider(firstScope.ServiceProvider, Microsoft.Extensions.Options.Options.Create(new TypeManifestOptions()));
        var second = new CodecProvider(secondScope.ServiceProvider, Microsoft.Extensions.Options.Options.Create(new TypeManifestOptions()));
        var caller = new object();
        Assert.NotSame(OrleansGeneratedCodeHelper.GetService<TransientDependency>(caller, first), OrleansGeneratedCodeHelper.GetService<TransientDependency>(caller, first));
        var scoped = OrleansGeneratedCodeHelper.GetService<ScopedDependency>(caller, first);
        Assert.Same(scoped, OrleansGeneratedCodeHelper.GetService<ScopedDependency>(caller, first));
        Assert.NotSame(scoped, OrleansGeneratedCodeHelper.GetService<ScopedDependency>(caller, second));
    }

    private static MyInvokableProxyBase CreateProxy(CopyContextPool pool, CodecProvider provider) =>
        new OrleansCodeGen.Orleans.Serialization.UnitTests.Proxy_ICacheResolutionProxy(pool, provider);
    private static object GetPayloadCopier(MyInvokableProxyBase proxy) =>
        Assert.Single(proxy.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance), field => field.FieldType == typeof(IDeepCopier<CacheResolutionPayload>)).GetValue(proxy)!;

    public sealed class TransientDependency { }
    public sealed class ScopedDependency { }
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
