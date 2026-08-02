using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Dashboard;

var builder = WebApplication.CreateBuilder(args);

// <SecureDashboard>
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DashboardOperators", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("OrleansOperator");
    });
});

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddDashboard(options =>
        {
            options.HideTrace = true;
            options.CounterUpdateIntervalMs = 2_000;
            options.HistoryLength = 100;
        });
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapOrleansDashboard("/dashboard")
    .RequireAuthorization("DashboardOperators");
// </SecureDashboard>

await app.RunAsync();
