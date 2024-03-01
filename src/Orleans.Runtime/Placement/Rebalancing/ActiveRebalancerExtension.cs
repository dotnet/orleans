using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement.Rebalancing;

internal interface IActiveRebalancerExtension : IGrainExtension
{
    /// <summary>
    /// There are 2 effects that we achive by doing this:
    /// <list type="number">
    /// <item>
    /// While rare, it could happen that a silo (call it B) doesnt get/send any messages (that we care about).
    /// The implication is that the exchange protocol initiator silo (call it A), when it tries to get a reference (rebalancer grain ref) to silo B, it may get a reference to itself instead.
    /// To play it safe, we basically 'ping' this grain upon rebalancer gateway startup.
    /// </item>
    /// <item>
    /// We want to activate the management grain, so each silo will have same number of actications when started.
    /// In localhost clustering, the primary silo will have 1 more activation "sys.svc.clustering.dev", but that is fine as it wont be activated in real-world systems.
    /// We have chosen 'GetActivationAddress' because it wont go through the network, so it cheap to do here, and we ignore it as we are interested in just management becoming active.
    /// </item>
    /// </list>
    /// </summary>
    ValueTask Activate();

    /// <summary>
    /// Meant for use in testing only!
    /// </summary>
    ValueTask ResetCounters();
}

internal sealed class ActiveRebalancerExtension : IActiveRebalancerExtension
{
    private readonly IGrainContext _context;
    private readonly IGrainFactory _grainFactory;
    private readonly Func<FrequencySink> _getter;
    private readonly Action<FrequencySink> _setter;

    public ActiveRebalancerExtension(
        IGrainContext context,
        IGrainFactory grainFactory,
        Func<FrequencySink> getter,
        Action<FrequencySink> setter)
    {
        _context = context;
        _grainFactory = grainFactory;
        _getter = getter;
        _setter = setter;
    }

    public ValueTask Activate()
    {
        var _managementGrain = _grainFactory.GetGrain<IManagementGrain>(0);
        _managementGrain.GetActivationAddress(_context.GrainReference).AsTask().Ignore();

        return ValueTask.CompletedTask;
    }

    public ValueTask ResetCounters()
    {
        var sink = _getter.Invoke();
        var newSink = new FrequencySink(sink.Capacity);

        _setter.Invoke(newSink);

        return ValueTask.CompletedTask;
    }
}
