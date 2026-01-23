#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Dashboard.Implementation;
using Orleans.Dashboard.Implementation.Details;
using Orleans.Dashboard.Metrics;
using Orleans.Dashboard.Metrics.Details;
using Orleans.Dashboard.Model;
using System.Diagnostics.CodeAnalysis;
using Orleans.Dashboard.Core;
using Microsoft.AspNetCore.Mvc;
using Orleans.Configuration.Internal;

// ReSharper disable CheckNamespace
namespace Orleans.Dashboard;

/// <summary>
/// Provides extension methods for configuring and integrating the Orleans Dashboard.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Orleans Dashboard services to the silo builder.
    /// </summary>
    /// <param name="siloBuilder">The silo builder.</param>
    /// <param name="configureOptions">Optional configuration action for <see cref="DashboardOptions"/>.</param>
    /// <returns>The silo builder for method chaining.</returns>
    public static ISiloBuilder AddDashboard(this ISiloBuilder siloBuilder, Action<DashboardOptions>? configureOptions = null)
    {
        siloBuilder.Services.AddOrleansDashboardForSiloCore(configureOptions);
        return siloBuilder;
    }

    internal static IServiceCollection AddOrleansDashboardForSiloCore(
        this IServiceCollection services,
        Action<DashboardOptions>? configureOptions = null)
    {
        services.AddGrainService<SiloGrainService>();
        services.AddHostedService<DashboardHost>();
        services.Configure(configureOptions ?? (x => { }));
        services.AddSingleton<DashboardTelemetryExporter>();
        services.AddOptions<GrainProfilerOptions>();

        services.AddSingleton<EmbeddedAssetProvider>();
        services.AddSingleton<SiloStatusOracleSiloDetailsProvider>();
        services.AddSingleton<MembershipTableSiloDetailsProvider>();
        services.AddSingleton<IDashboardClient, DashboardClient>();
        services.AddSingleton<DashboardLogger>();
        services.AddFromExisting<ILoggerProvider, DashboardLogger>();
        services.AddSingleton<IGrainProfiler, GrainProfiler>();
        services.AddSingleton(c => (ILifecycleParticipant<ISiloLifecycle>)c.GetRequiredService<IGrainProfiler>());
        services.AddSingleton<IIncomingGrainCallFilter, GrainProfilerFilter>();

        services.AddSingleton<ISiloGrainClient, SiloGrainClient>();

        services.AddSingleton<ISiloDetailsProvider>(c
            => c.GetService<IMembershipTable>() switch
            {
                not null =>
                c.GetRequiredService<MembershipTableSiloDetailsProvider>(),
                null => c.GetRequiredService<SiloStatusOracleSiloDetailsProvider>(),
            });

        services.TryAddSingleton(GrainProfilerFilter.DefaultGrainMethodFormatter);

        return services;
    }

    /// <summary>
    /// Maps Orleans Dashboard endpoints using ASP.NET Core minimal APIs.
    /// Returns an <see cref="IEndpointConventionBuilder"/> that can be used to apply authentication,
    /// authorization, or other endpoint configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// // Basic usage
    /// app.MapOrleansDashboard();
    ///
    /// // With authentication
    /// app.MapOrleansDashboard().RequireAuthorization();
    ///
    /// // With custom base path
    /// app.MapOrleansDashboard(routePrefix: "/dashboard");
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapOrleansDashboard(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string? routePrefix = null)
    {
        // Create static assets provider
        var assets = endpoints.ServiceProvider.GetService<EmbeddedAssetProvider>()
            ?? throw new InvalidOperationException("Orleans Dashboard services have not been registered. " +
                "Please call AddDashboard on ISiloBuilder or IClientBuilder.");

        // Create a route group for all dashboard endpoints
        var group = endpoints.MapGroup(routePrefix ?? "");

        // Static assets - these match the paths referenced in the built CSS/HTML
        // When a routePrefix is specified, redirect requests without trailing slash to include it.
        // This ensures relative asset paths (like index.min.js) resolve correctly.
        group.MapGet("/", (HttpContext ctx) =>
        {
            if (!string.IsNullOrEmpty(routePrefix) && ctx.Request.Path.Value?.EndsWith('/') == false)
            {
                // Redirect to the same path with a trailing slash, preserving the query string
                var redirectUrl = $"{ctx.Request.PathBase}{ctx.Request.Path}/{ctx.Request.QueryString}";
                return Results.Redirect(redirectUrl, permanent: true);
            }
            return assets.ServeAsset("index.html", ctx);
        });
        group.MapGet("/index.html", (HttpContext ctx) => assets.ServeAsset("index.html", ctx));
        group.MapGet("/favicon.ico", (HttpContext ctx) => assets.ServeAsset("favicon.ico", ctx));
        group.MapGet("/index.min.js", (HttpContext ctx) => assets.ServeAsset("index.min.js", ctx));
        group.MapGet("/index.css", (HttpContext ctx) => assets.ServeAsset("index.css", ctx));

        // Font files - catch-all route for /fonts/ directory
        group.MapGet("/fonts/{**path}", (string path, HttpContext ctx) => assets.ServeAsset($"fonts.{path}", ctx));

        // Image files - catch-all route for /img/ directory
        group.MapGet("/img/{**path}", (string path, HttpContext ctx) => assets.ServeAsset($"img.{path}", ctx));

        // API endpoints
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true,
            Converters = { new TimeSpanConverter() }
        };

        group.MapGet("/version", () => Results.Json(
            new { version = typeof(EmbeddedAssetProvider).Assembly.GetName().Version?.ToString() },
            jsonOptions));

        group.MapGet("/DashboardCounters", async ([FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.DashboardCounters();
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/ClusterStats", async ([FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.ClusterStats();
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/Reminders", async ([FromServices] IDashboardClient client) => await GetRemindersPage(1, client, jsonOptions));
        group.MapGet("/Reminders/{page:int}", async (int page, [FromServices] IDashboardClient client) => await GetRemindersPage(page, client, jsonOptions));

        group.MapGet("/HistoricalStats/{*path}", async (string path, [FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.HistoricalStats(path);
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/SiloProperties/{*address}", async (string address, [FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.SiloProperties(address);
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/SiloStats/{*address}", async (string address, [FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.SiloStats(address);
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/SiloCounters/{*address}", async (string address, [FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.GetCounters(address);
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/GrainStats/{*grainName}", async (string grainName, [FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.GrainStats(grainName);
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/TopGrainMethods", async ([FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.TopGrainMethods(take: 5);
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/GrainState", async (HttpContext context, [FromServices] IDashboardClient client) =>
        {
            try
            {
                context.Request.Query.TryGetValue("grainId", out var grainId);
                context.Request.Query.TryGetValue("grainType", out var grainType);
                var result = await client.GetGrainState(grainId, grainType);
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/GrainTypes", async ([FromServices] IDashboardClient client) =>
        {
            try
            {
                var result = await client.GetGrainTypes();
                return Results.Json(result.Value, jsonOptions);
            }
            catch (SiloUnavailableException)
            {
                return CreateUnavailableResult(true);
            }
        });

        group.MapGet("/Trace", async (HttpContext context, [FromServices] IOptions<DashboardOptions> opts, [FromServices] DashboardLogger logger) =>
        {
            if (opts.Value.HideTrace)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await StreamTraceAsync(context, logger);
            return Results.Empty;
        });

        return group;
    }

    private static async Task<IResult> GetRemindersPage(int page, IDashboardClient client, JsonSerializerOptions jsonOptions)
    {
        try
        {
            var result = await client.GetReminders(page, 50);
            return Results.Json(result.Value, jsonOptions);
        }
        catch (SiloUnavailableException)
        {
            return CreateUnavailableResult(true);
        }
        catch
        {
            // If reminders are not configured, return empty response
            return Results.Json(new ReminderResponse { Reminders = [], Count = 0 }, jsonOptions);
        }
    }

    private static async Task StreamTraceAsync(HttpContext context, DashboardLogger logger)
    {
        var token = context.RequestAborted;

        try
        {
            await using var writer = new TraceWriter(logger, context);
            await writer.WriteAsync("""
                   ____       _                        _____            _     _                         _
                  / __ \     | |                      |  __ \          | |   | |                       | |
                 | |  | |_ __| | ___  __ _ _ __  ___  | |  | | __ _ ___| |__ | |__   ___   __ _ _ __ __| |
                 | |  | | '__| |/ _ \/ _` | '_ \/ __| | |  | |/ _` / __| '_ \| '_ \ / _ \ / _` | '__/ _` |
                 | |__| | |  | |  __/ (_| | | | \__ \ | |__| | (_| \__ \ | | | |_) | (_) | (_| | | | (_| |
                  \____/|_|  |_|\___|\__,_|_| |_|___/ |_____/ \__,_|___/_| |_|_.__/ \___/ \__,_|_|  \__,_|

                You are connected to the Orleans Dashboard log streaming service
                """);

            await Task.Delay(TimeSpan.FromMinutes(60), token);
            await writer.WriteAsync("Disconnecting after 60 minutes\r\n");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static IResult CreateUnavailableResult(bool lostConnectivity)
    {
        var message = lostConnectivity
            ? "The dashboard has lost connectivity with the Orleans cluster"
            : "The dashboard is still trying to connect to the Orleans cluster";

        return Results.Text(message, "text/plain", statusCode: 503);
    }

    /// <summary>
    /// Adds Orleans Dashboard services to an Orleans client builder.
    /// This allows you to host the Orleans Dashboard application on an Orleans client, so long as the silos also have the dashboard added.
    /// </summary>
    /// <param name="clientBuilder">The client builder.</param>
    /// <param name="configureOptions">Optional configuration action for <see cref="DashboardOptions"/>.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IClientBuilder AddDashboard(this IClientBuilder clientBuilder, Action<DashboardOptions>? configureOptions = null)
    {
        clientBuilder.Services.Configure(configureOptions ?? (x => { }));
        clientBuilder.Services.AddSingleton<DashboardLogger>();
        clientBuilder.Services.AddFromExisting<ILoggerProvider, DashboardLogger>();
        clientBuilder.Services.AddSingleton<IDashboardClient, DashboardClient>();
        clientBuilder.Services.AddSingleton<EmbeddedAssetProvider>();

        return clientBuilder;
    }
}
