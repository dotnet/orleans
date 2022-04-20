namespace Orleans.ShoppingCart.Silo.Extensions;

internal static class HttpContextAccessorExtensions
{
    internal static string? TryGetUserId(
        this IHttpContextAccessor? httpContextAccessor) =>
        httpContextAccessor
            ?.HttpContext
            ?.User
            .FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "demo-shared-user";
}
