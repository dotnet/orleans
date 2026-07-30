using Orleans;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Persistence;

// Grain interfaces for documentation examples
public interface IUserGrain : IGrainWithStringKey
{
    Task<string> GetNameAsync();
    Task SetNameAsync(string name);
}

// <user_grain_multiple_states>
public class UserGrain : Grain, IUserGrain
{
    private readonly IPersistentState<ProfileState> _profile;
    private readonly IPersistentState<CartState> _cart;

    public UserGrain(
        [PersistentState("profile", "profileStore")] IPersistentState<ProfileState> profile,
        [PersistentState("cart", "cartStore")] IPersistentState<CartState> cart)
    {
        _profile = profile;
        _cart = cart;
    }

    public Task<string> GetNameAsync() => Task.FromResult(_profile.State.Name);

    public async Task SetNameAsync(string name)
    {
        _profile.State.Name = name;
        await _profile.WriteStateAsync();
    }
}
// </user_grain_multiple_states>

// Separate example grains for simpler demonstrations

// <user_grain_constructor_injection>
public class UserGrainSimple : Grain, IUserGrain
{
    private readonly IPersistentState<ProfileState> _profile;

    public UserGrainSimple(
        [PersistentState("profile", "profileStore")]
        IPersistentState<ProfileState> profile)
    {
        _profile = profile;
    }

    public Task<string> GetNameAsync() => Task.FromResult(_profile.State.Name);

    public async Task SetNameAsync(string name)
    {
        _profile.State.Name = name;
        await _profile.WriteStateAsync();
    }
}
// </user_grain_constructor_injection>

// <user_grain_complete>
public class UserGrainComplete : Grain, IUserGrain
{
    private readonly IPersistentState<ProfileState> _profile;

    public UserGrainComplete(
        [PersistentState("profile", "profileStore")]
        IPersistentState<ProfileState> profile)
    {
        _profile = profile;
    }

    public Task<string> GetNameAsync() => Task.FromResult(_profile.State.Name);

    public async Task SetNameAsync(string name)
    {
        _profile.State.Name = name;
        await _profile.WriteStateAsync();
    }
}
// </user_grain_complete>

// <storage_provider_attribute>
[StorageProvider(ProviderName = "store1")]
public class MyGrain : Grain<MyGrainState>, IMyGrain
{
    public Task DoSomethingAsync() => Task.CompletedTask;
}
// </storage_provider_attribute>

public class MyGrainState
{
    public string Data { get; set; }
}

public interface IMyGrain : IGrainWithStringKey
{
    Task DoSomethingAsync();
}

// Helper class for standalone method snippets
public class MethodExamples : Grain, IUserGrain
{
    private readonly IPersistentState<ProfileState> _profile;

    public MethodExamples(
        [PersistentState("profile", "profileStore")]
        IPersistentState<ProfileState> profile)
    {
        _profile = profile;
    }

    // <read_state_example>
    public Task<string> GetNameAsync() => Task.FromResult(_profile.State.Name);
    // </read_state_example>

    // <write_state_example>
    public async Task SetNameAsync(string name)
    {
        _profile.State.Name = name;
        await _profile.WriteStateAsync();
    }
    // </write_state_example>
}
