using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>A durable grain placed on the silo hosting its journal's partition.</summary>
public interface IGarnetLocalityGrain : IGrainWithGuidKey
{
    Task<string> GetSilo();

    Task SetValue(int value);

    Task<int> GetValue();
}

[GarnetJournalPlacement]
public sealed class GarnetLocalityGrain(
    [FromKeyedServices("value")] IPersistentState<int> state,
    ILocalSiloDetails silo) : DurableGrain, IGarnetLocalityGrain
{
    public Task<string> GetSilo() => Task.FromResult(silo.SiloAddress.ToParsableString());

    public async Task SetValue(int value)
    {
        state.State = value;
        await WriteStateAsync();
    }

    public Task<int> GetValue() => Task.FromResult(state.State);
}
