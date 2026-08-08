using System.Reflection;

namespace Orleans.Docs.Snippets.Interceptors;

// <per_grain_filter>
public sealed class MyFilteredGrain
    : Grain, IMyFilteredGrain, IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        await context.Invoke();

        // Change the result of the call from 7 to 38.
        if (string.Equals(
            context.InterfaceMethod.Name,
            nameof(IMyFilteredGrain.GetFavoriteNumber)))
        {
            context.Result = 38;
        }
    }

    public Task<int> GetFavoriteNumber() => Task.FromResult(7);
}
// </per_grain_filter>

// <access_control_contract>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuthorizeGrainCallAttribute : Attribute
{
    public AuthorizeGrainCallAttribute(string policy) => Policy = policy;

    public string Policy { get; }
}

public interface IGrainCallAuthorizationService
{
    // Implement this service using an identity validated by trusted infrastructure.
    ValueTask<bool> AuthorizeAsync(
        IIncomingGrainCallContext context,
        string policy);
}
// </access_control_contract>

// <access_control_grain>
public sealed class MyAccessControlledGrain(
    IGrainCallAuthorizationService authorizationService)
    : Grain, IMyFilteredGrain, IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var authorization = context.ImplementationMethod
            .GetCustomAttribute<AuthorizeGrainCallAttribute>();

        if (authorization is not null
            && !await authorizationService.AuthorizeAsync(
                context,
                authorization.Policy))
        {
            throw new UnauthorizedAccessException(
                "The caller isn't authorized to invoke this operation.");
        }

        await context.Invoke();
    }

    [AuthorizeGrainCall("Admin")]
    public Task<int> GetFavoriteNumber() => Task.FromResult(7);
}
// </access_control_grain>
