using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.General;

internal sealed class Phase2TestGrain : Grain, ITestGrain, IGuidTestGrain
{
    Task<long> ITestGrain.GetKey() => Task.FromResult(0L);

    Task<Guid> IGuidTestGrain.GetKey() => Task.FromResult(Guid.Empty);

    public Task<string> GetLabel() => Task.FromResult(string.Empty);

    public Task SetLabel(string label) => Task.CompletedTask;

    public Task<string> GetRuntimeInstanceId() => Task.FromResult(string.Empty);

    public Task<string> GetActivationId() => Task.FromResult(string.Empty);

    public Task<ITestGrain> GetGrainReference() => Task.FromResult<ITestGrain>(null!);

    public Task<Tuple<string, string>> TestRequestContext() =>
        Task.FromResult(Tuple.Create(string.Empty, string.Empty));

    public Task<IGrain[]> GetMultipleGrainInterfaces_Array() =>
        Task.FromResult(Array.Empty<IGrain>());

    public Task<List<IGrain>> GetMultipleGrainInterfaces_List() =>
        Task.FromResult(new List<IGrain>());

    public Task StartTimer() => Task.CompletedTask;

    public Task DoLongAction(TimeSpan timespan, string str) => Task.CompletedTask;

    public Task<SiloAddress> GetSiloAddress() => Task.FromResult(SiloAddress.Zero);
}
