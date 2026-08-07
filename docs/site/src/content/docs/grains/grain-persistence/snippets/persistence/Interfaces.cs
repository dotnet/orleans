namespace Orleans.Docs.Snippets.Persistence;

// <persistent_state_interface>
public interface IPersistentState<TState> : IStorage<TState>
{
}

public interface IStorage<TState> : IStorage
{
    TState State { get; set; }
}

public interface IStorage
{
    string? Etag { get; }

    bool RecordExists { get; }

    Task ClearStateAsync();

    Task ClearStateAsync(CancellationToken cancellationToken) =>
        ClearStateAsync();

    Task WriteStateAsync();

    Task WriteStateAsync(CancellationToken cancellationToken) =>
        WriteStateAsync();

    Task ReadStateAsync();

    Task ReadStateAsync(CancellationToken cancellationToken) =>
        ReadStateAsync();
}
// </persistent_state_interface>
