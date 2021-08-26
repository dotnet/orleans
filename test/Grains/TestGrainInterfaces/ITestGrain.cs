using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface ITestGrain : IGrainWithIntegerKey
    {
        // duplicate to verify identity
        Task<long> GetKey();

        // separate label that can be set
        Task<string> GetLabel();

        Task SetLabel(string label);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetActivationId();

        Task<ITestGrain> GetGrainReference();

        Task<Tuple<string, string>> TestRequestContext();

        Task<IGrain[]> GetMultipleGrainInterfaces_Array();

        Task<List<IGrain>> GetMultipleGrainInterfaces_List();

        Task StartTimer();

        Task DoLongAction(TimeSpan timespan, string str);
    }

    public interface ITestGrainLongOnActivateAsync : IGrainWithIntegerKey
    {
        Task<long> GetKey();
    }

    public interface IGuidTestGrain : IGrainWithGuidKey
    {
        // duplicate to verify identity
        Task<Guid> GetKey();

        // separate label that can be set
        Task<string> GetLabel();

        Task SetLabel(string label);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetActivationId();
    }

    public interface IOneWayGrain : IGrainWithGuidKey
    {
        [OneWay]
        Task Notify(ISimpleGrainObserver observer);

        [OneWay]
        ValueTask NotifyValueTask(ISimpleGrainObserver observer);

        [OneWay]
        Task ThrowsOneWay();

        [OneWay]
        ValueTask ThrowsOneWayValueTask();

        Task<bool> NotifyOtherGrain(IOneWayGrain otherGrain, ISimpleGrainObserver observer);

        Task<bool> NotifyOtherGrainValueTask(IOneWayGrain otherGrain, ISimpleGrainObserver observer);

        Task<IOneWayGrain> GetOtherGrain();

        Task NotifyOtherGrain();

        Task<int> GetCount();

        Task Deactivate();

        Task<SiloAddress> GetSiloAddress();

        Task<SiloAddress> GetPrimaryForGrain();

        Task<string> GetActivationId();

        Task<string> GetActivationAddress(IGrain grain);

        Task SignalSelfViaOther();

        [OneWay]
        Task SendSignalTo(IOneWayGrain grain);

        [AlwaysInterleave]
        Task<(int NumSignals, string SignallerId)> WaitForSignal();

        [AlwaysInterleave]
        Task Signal(string id);
    }

    public interface ICanBeOneWayGrain : IGrainWithGuidKey
    {
        Task Notify(ISimpleGrainObserver observer);

        ValueTask NotifyValueTask(ISimpleGrainObserver observer);

        Task Throws();

        ValueTask ThrowsValueTask();

        Task<int> GetCount();
    }
}
