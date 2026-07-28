using System.Reflection;
using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Interceptors;

// <per_grain_filter>
public class MyFilteredGrain
    : Grain, IMyFilteredGrain, Orleans.IIncomingGrainCallFilter
{
    public async Task Invoke(Orleans.IIncomingGrainCallContext context)
    {
        await context.Invoke();

        // Change the result of the call from 7 to 38.
        if (string.Equals(
            context.InterfaceMethod.Name,
            nameof(this.GetFavoriteNumber)))
        {
            context.Result = 38;
        }
    }

    public Task<int> GetFavoriteNumber() => Task.FromResult(7);
}
// </per_grain_filter>

// <access_control_attribute>
[AttributeUsage(AttributeTargets.Method)]
public class AdminOnlyAttribute : Attribute { }
// </access_control_attribute>

// <access_control_grain>
public class MyAccessControlledGrain
    : Grain, IMyFilteredGrain, Orleans.IIncomingGrainCallFilter
{
    public Task Invoke(Orleans.IIncomingGrainCallContext context)
    {
        // Check access conditions.
        var isAdminMethod =
            context.ImplementationMethod.GetCustomAttribute<AdminOnlyAttribute>();
        if (isAdminMethod is not null && RequestContext.Get("isAdmin") is not true)
        {
            throw new AccessDeniedException(
                $"Only admins can access {context.ImplementationMethod.Name}!");
        }

        return context.Invoke();
    }

    [AdminOnly]
    public Task<int> GetFavoriteNumber() => Task.FromResult(7);
}
// </access_control_grain>
