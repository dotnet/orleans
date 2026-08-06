using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public sealed class SerializerServiceProviderLifetimeTests
{
    [Fact]
    public void DisposingServiceProviderReleasesPooledSerializerState()
    {
        var codecProvider = CreateWeakReferenceToPooledCodecProvider();

        Collect();

        Assert.False(codecProvider.IsAlive);
    }

    [Fact]
    public void PooledSerializerStateCanBeReturnedAfterServiceProviderDisposal()
    {
        var serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        var session = serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
        var context = serviceProvider.GetRequiredService<CopyContextPool>().GetContext();

        serviceProvider.Dispose();

        session.Dispose();
        context.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakReferenceToPooledCodecProvider()
    {
        var serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        var codecProvider = serviceProvider.GetRequiredService<CodecProvider>();

        using (serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession())
        {
        }

        using (serviceProvider.GetRequiredService<CopyContextPool>().GetContext())
        {
        }

        var result = new WeakReference(codecProvider);
        serviceProvider.Dispose();
        return result;
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
