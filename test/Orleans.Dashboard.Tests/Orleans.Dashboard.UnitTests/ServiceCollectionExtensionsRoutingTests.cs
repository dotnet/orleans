using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Orleans;
using Orleans.Concurrency;
using Orleans.Dashboard;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Implementation;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class ServiceCollectionExtensionsRoutingTests
{
    private const string AuthorizationPolicy = "dashboard-reader";

    [Fact]
    public void MapOrleansDashboard_WithoutDashboardRegistration_ThrowsActionableInvalidOperationException()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var endpoints = new TestEndpointRouteBuilder(provider);

        var exception = Assert.Throws<InvalidOperationException>(() => endpoints.MapOrleansDashboard("/dashboard"));

        Assert.Equal(
            "Orleans Dashboard services have not been registered. Please call AddDashboard on ISiloBuilder or IClientBuilder.",
            exception.Message);
        Assert.Empty(endpoints.DataSources);
    }

    [Fact]
    public void MapOrleansDashboard_NoPrefix_MapsExactRootStaticAndApiEndpointInventory()
    {
        using var dashboard = DashboardRoutes.Create();

        Assert.Equal(ExpectedRoutePatterns(""), dashboard.RoutePatterns);
        Assert.Contains("/", dashboard.RoutePatterns);
        Assert.Contains("/index.css", dashboard.RoutePatterns);
        Assert.Contains("/DashboardCounters", dashboard.RoutePatterns);
        Assert.Contains("/Trace", dashboard.RoutePatterns);
    }

    [Fact]
    public void MapOrleansDashboard_CustomPrefix_MapsExactNestedInventoryWithoutRootEndpoints()
    {
        using var dashboard = DashboardRoutes.Create("/ops/orleans");

        Assert.Equal(ExpectedRoutePatterns("/ops/orleans"), dashboard.RoutePatterns);
        Assert.All(dashboard.RoutePatterns, pattern =>
            Assert.StartsWith("/ops/orleans/", pattern, StringComparison.Ordinal));
        Assert.DoesNotContain("/DashboardCounters", dashboard.RoutePatterns);
        Assert.DoesNotContain("/", dashboard.RoutePatterns);
    }

    [Fact]
    public async Task MapOrleansDashboard_PrefixWithoutTrailingSlash_RedirectsAndPreservesPathBaseAndQueryString()
    {
        using var dashboard = DashboardRoutes.Create("/ops/orleans");

        var response = await dashboard.ExecuteAsync(
            "/ops/orleans/",
            "/ops/orleans",
            "?cluster=blue&view=grain%20calls",
            pathBase: "/gateway",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status301MovedPermanently, response.StatusCode);
        Assert.Equal(
            "/gateway/ops/orleans/?cluster=blue&view=grain%20calls",
            response.Headers.Location.ToString());
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task MapOrleansDashboard_StaticAsset_ReturnsExactStatusContentHeadersAndBody()
    {
        using var dashboard = DashboardRoutes.Create("/dashboard");
        var expectedBody = ReadEmbeddedAsset("index.css");

        var response = await dashboard.ExecuteAsync(
            "/dashboard/index.css",
            "/dashboard/index.css",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("text/css", response.ContentType);
        Assert.Equal(expectedBody.Length, response.ContentLength);
        var cacheControl = CacheControlHeaderValue.Parse(response.Headers.CacheControl.ToString());
        Assert.True(cacheControl.NoCache);
        Assert.True(cacheControl.NoStore);
        Assert.True(EntityTagHeaderValue.TryParse(response.Headers.ETag.ToString(), out var entityTag));
        Assert.Equal(expectedBody, response.Body);

        var cachedResponse = await dashboard.ExecuteAsync(
            "/dashboard/index.css",
            "/dashboard/index.css",
            requestAborted: TestContext.Current.CancellationToken,
            ifNoneMatch: entityTag.ToString());

        Assert.Equal(StatusCodes.Status304NotModified, cachedResponse.StatusCode);
        Assert.Empty(cachedResponse.Body);
    }

    [Theory]
    [InlineData("/index.html", "index.html", "text/html")]
    [InlineData("/favicon.ico", "favicon.ico", "image/x-icon")]
    [InlineData("/index.min.js", "index.min.js", "text/javascript")]
    public async Task MapOrleansDashboard_NamedStaticAsset_ReturnsExactMappedResource(
        string route,
        string resourceName,
        string contentType)
    {
        using var dashboard = DashboardRoutes.Create();
        var expectedBody = ReadEmbeddedAsset(resourceName);

        var response = await dashboard.ExecuteAsync(
            route,
            route,
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(contentType, response.ContentType);
        Assert.Equal(expectedBody.Length, response.ContentLength);
        Assert.Equal(expectedBody, response.Body);
    }

    [Theory]
    [InlineData("/", "/", null)]
    [InlineData("/dashboard/", "/dashboard/", "/dashboard")]
    public async Task MapOrleansDashboard_RootWithCanonicalSlash_ReturnsIndexWithoutRedirect(
        string routePattern,
        string requestPath,
        string? routePrefix)
    {
        using var dashboard = DashboardRoutes.Create(routePrefix);
        var expectedBody = ReadEmbeddedAsset("index.html");

        var response = await dashboard.ExecuteAsync(
            routePattern,
            requestPath,
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("text/html", response.ContentType);
        Assert.Equal(0, response.Headers.Location.Count);
        Assert.Equal(expectedBody, response.Body);
    }

    [Theory]
    [InlineData("/fonts/{**path}", "/fonts/fa-solid-900.woff2", "path", "fa-solid-900.woff2", "fonts.fa-solid-900.woff2")]
    [InlineData("/img/{**path}", "/img/OrleansLogo.png", "path", "OrleansLogo.png", "img.OrleansLogo.png")]
    public async Task MapOrleansDashboard_NestedStaticAsset_ForwardsExactResourcePrefixAndPath(
        string routePattern,
        string requestPath,
        string routeParameter,
        string routeValue,
        string resourceName)
    {
        using var dashboard = DashboardRoutes.Create();
        var expectedBody = ReadEmbeddedAsset(resourceName);

        var response = await dashboard.ExecuteAsync(
            routePattern,
            requestPath,
            routeValues: new Dictionary<string, object?> { [routeParameter] = routeValue },
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(expectedBody.Length, response.ContentLength);
        Assert.Equal(expectedBody, response.Body);
    }

    [Fact]
    public async Task MapOrleansDashboard_Version_ReturnsAssemblyVersionInExactJsonShape()
    {
        using var dashboard = DashboardRoutes.Create();

        var response = await dashboard.ExecuteAsync(
            "/version",
            "/version",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(["version"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            typeof(EmbeddedAssetProvider).Assembly.GetName().Version?.ToString(),
            json.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task MapOrleansDashboard_ApiRequest_ForwardsNormalizedFilterArgumentsAndFixedTake()
    {
        var client = new TestDashboardClient();
        using var dashboard = DashboardRoutes.Create(client: client);

        var countersResponse = await dashboard.ExecuteAsync(
            "/DashboardCounters",
            "/DashboardCounters",
            "?exclude=%20Orleans.Runtime%20&exclude=&exclude=%20%20&exclude=My.Grain",
            requestAborted: TestContext.Current.CancellationToken);
        var methodsResponse = await dashboard.ExecuteAsync(
            "/TopGrainMethods",
            "/TopGrainMethods",
            "?exclude=%20System%20&exclude=Application",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, countersResponse.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, methodsResponse.StatusCode);
        Assert.Equal(new[] { "Orleans.Runtime.", "My.Grain." }, client.DashboardCounterExclusions);
        Assert.Equal(new[] { "System.", "Application." }, client.TopMethodExclusions);
        Assert.Equal(5, client.TopMethodTake);
        Assert.Equal(1, client.DashboardCountersCalls);
        Assert.Equal(1, client.TopGrainMethodsCalls);
        Assert.Equal("{}", methodsResponse.BodyText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?exclude=")]
    [InlineData("?exclude=%20%20%20")]
    public async Task MapOrleansDashboard_EmptyFilterValues_ForwardsEmptyArray(string queryString)
    {
        var client = new TestDashboardClient();
        using var dashboard = DashboardRoutes.Create(client: client);

        var response = await dashboard.ExecuteAsync(
            "/GrainTypes",
            "/GrainTypes",
            queryString,
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Empty(client.GrainTypeExclusions);
        Assert.Equal(1, client.GrainTypesCalls);
        Assert.Equal("[]", response.BodyText);
    }

    [Fact]
    public async Task MapOrleansDashboard_GrainState_ForwardsExactQueryArgumentsAndReturnsExactPayload()
    {
        var client = new TestDashboardClient
        {
            GrainStateResult = """{"balance":17,"currency":"USD"}""",
        };
        using var dashboard = DashboardRoutes.Create(client: client);

        var response = await dashboard.ExecuteAsync(
            "/GrainState",
            "/GrainState",
            "?grainId=customer%2F42&grainType=Acme.Grains.Customer%2C%20Acme",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("customer/42", client.GrainStateId);
        Assert.Equal("Acme.Grains.Customer, Acme", client.GrainStateType);
        Assert.Equal(1, client.GrainStateCalls);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(client.GrainStateResult, json.RootElement.GetString());
    }

    [Theory]
    [InlineData("/HistoricalStats/{*path}", "/HistoricalStats/silo-a", nameof(IDashboardClient.HistoricalStats), "path", "silo-a", "[]")]
    [InlineData("/SiloProperties/{*address}", "/SiloProperties/silo-b", nameof(IDashboardClient.SiloProperties), "address", "silo-b", "{}")]
    [InlineData("/SiloMetadata/{*address}", "/SiloMetadata/silo-c", nameof(IDashboardClient.SiloMetadata), "address", "silo-c", "{}")]
    [InlineData("/SiloStats/{*address}", "/SiloStats/silo-d", nameof(IDashboardClient.SiloStats), "address", "silo-d", "{}")]
    [InlineData("/SiloCounters/{*address}", "/SiloCounters/silo-e", nameof(IDashboardClient.GetCounters), "address", "silo-e", "[]")]
    [InlineData("/GrainStats/{*grainName}", "/GrainStats/Acme.Customer", nameof(IDashboardClient.GrainStats), "grainName", "Acme.Customer", "{}")]
    [InlineData("/LifecycleStages", "/LifecycleStages", nameof(IDashboardClient.GetLifecycleStages), null, null, "[]")]
    public async Task MapOrleansDashboard_RemainingApiRoutes_ForwardExactMethodAndRouteValue(
        string routePattern,
        string requestPath,
        string expectedOperation,
        string? routeParameter,
        string? routeValue,
        string expectedJson)
    {
        var client = new TestDashboardClient();
        using var dashboard = DashboardRoutes.Create(client: client);
        var routeValues = routeParameter is null
            ? null
            : new Dictionary<string, object?> { [routeParameter] = routeValue };

        var response = await dashboard.ExecuteAsync(
            routePattern,
            requestPath,
            routeValues: routeValues,
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal(expectedOperation, client.LastOperation);
        Assert.Equal(routeValue, client.LastArgument);
        Assert.Equal(expectedJson, response.BodyText);
    }

    [Fact]
    public async Task MapOrleansDashboard_RemindersResponse_HasExactJsonShapeTimeSpanAndForwardedPaging()
    {
        var client = new TestDashboardClient
        {
            ReminderResult = new ReminderResponse
            {
                Count = 1,
                Reminders =
                [
                    new ReminderInfo
                    {
                        GrainReference = "customer/42",
                        Name = "billing",
                        PrimaryKey = "42",
                        StartAt = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
                        Period = new TimeSpan(1, 2, 3, 4, 5, 6),
                    },
                ],
            },
        };
        using var dashboard = DashboardRoutes.Create(client: client);

        var response = await dashboard.ExecuteAsync(
            "/Reminders/{page:int}",
            "/Reminders/7",
            routeValues: new Dictionary<string, object?> { ["page"] = "7" },
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(7, client.ReminderPage);
        Assert.Equal(50, client.ReminderPageSize);
        Assert.Equal(1, client.ReminderCalls);
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        var reminder = Assert.Single(root.GetProperty("reminders").EnumerateArray());
        Assert.Equal("customer/42", reminder.GetProperty("grainReference").GetString());
        Assert.Equal("billing", reminder.GetProperty("name").GetString());
        Assert.Equal("42", reminder.GetProperty("primaryKey").GetString());
        Assert.Equal("2025-03-04T05:06:07Z", reminder.GetProperty("startAt").GetString());
        Assert.Equal("1.02:03:04.0050060", reminder.GetProperty("period").GetString());
    }

    [Fact]
    public void MapOrleansDashboard_ReturnedGroupRequireAuthorization_AppliesNamedPolicyToEveryEndpoint()
    {
        using var dashboard = DashboardRoutes.Create(
            "/dashboard",
            requireAuthorization: true);

        Assert.Equal(ExpectedRoutePatterns("/dashboard"), dashboard.RoutePatterns);
        Assert.All(dashboard.Endpoints, endpoint =>
        {
            var authorizeData = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(AuthorizationPolicy, authorizeData.Policy);
        });
    }

    [Fact]
    public async Task MapOrleansDashboard_ProtectedGroup_UnauthorizedRequestsChallengeWithoutExecutingStaticOrApiEndpoints()
    {
        var client = new TestDashboardClient();
        using var dashboard = DashboardRoutes.Create(
            "/dashboard",
            client,
            requireAuthorization: true);

        var staticResponse = await dashboard.ExecuteAuthorizedAsync(
            "/dashboard/index.css",
            "/dashboard/index.css",
            authenticated: false,
            requestAborted: TestContext.Current.CancellationToken);
        var apiResponse = await dashboard.ExecuteAuthorizedAsync(
            "/dashboard/ClusterStats",
            "/dashboard/ClusterStats",
            authenticated: false,
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, staticResponse.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, apiResponse.StatusCode);
        Assert.Empty(staticResponse.Body);
        Assert.Empty(apiResponse.Body);
        Assert.Equal(0, client.TotalCalls);
    }

    [Fact]
    public async Task MapOrleansDashboard_ProtectedGroup_AuthorizedRequestsExecuteStaticAndApiEndpoints()
    {
        var client = new TestDashboardClient
        {
            ClusterStatsResult = new Dictionary<string, GrainTraceEntry>
            {
                ["CustomerGrain.Pay"] = new()
                {
                    Grain = "CustomerGrain",
                    Method = "Pay",
                    Count = 4,
                    ExceptionCount = 1,
                    ElapsedTime = 12.5,
                    PeriodKey = "2025-03-04T05:00Z",
                    Period = new DateTime(2025, 3, 4, 5, 0, 0, DateTimeKind.Utc),
                    SiloAddress = "127.0.0.1:11111@42",
                },
            },
        };
        using var dashboard = DashboardRoutes.Create(
            "/dashboard",
            client,
            requireAuthorization: true);

        var staticResponse = await dashboard.ExecuteAuthorizedAsync(
            "/dashboard/index.css",
            "/dashboard/index.css",
            authenticated: true,
            requestAborted: TestContext.Current.CancellationToken);
        var apiResponse = await dashboard.ExecuteAuthorizedAsync(
            "/dashboard/ClusterStats",
            "/dashboard/ClusterStats",
            authenticated: true,
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, staticResponse.StatusCode);
        Assert.Equal("text/css", staticResponse.ContentType);
        Assert.Equal(ReadEmbeddedAsset("index.css"), staticResponse.Body);
        Assert.Equal(StatusCodes.Status200OK, apiResponse.StatusCode);
        using var json = JsonDocument.Parse(apiResponse.Body);
        var trace = json.RootElement.GetProperty("CustomerGrain.Pay");
        Assert.Equal("CustomerGrain", trace.GetProperty("grain").GetString());
        Assert.Equal("Pay", trace.GetProperty("method").GetString());
        Assert.Equal(4, trace.GetProperty("count").GetInt64());
        Assert.Equal(1, client.ClusterStatsCalls);
    }

    [Fact]
    public async Task MapOrleansDashboard_ClientThrowsSiloUnavailableException_Returns503AndExactBody()
    {
        var client = new TestDashboardClient
        {
            ClusterStatsException = new SiloUnavailableException("cluster is restarting"),
        };
        using var dashboard = DashboardRoutes.Create(client: client);

        var response = await dashboard.ExecuteAsync(
            "/ClusterStats",
            "/ClusterStats",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/plain", response.ContentType);
        Assert.Equal(
            "The dashboard has lost connectivity with the Orleans cluster",
            response.BodyText);
        Assert.Equal(1, client.ClusterStatsCalls);
    }

    [Theory]
    [InlineData("/DashboardCounters", "/DashboardCounters", nameof(IDashboardClient.DashboardCounters), null, null)]
    [InlineData("/ClusterStats", "/ClusterStats", nameof(IDashboardClient.ClusterStats), null, null)]
    [InlineData("/Reminders", "/Reminders", nameof(IDashboardClient.GetReminders), null, null)]
    [InlineData("/HistoricalStats/{*path}", "/HistoricalStats/silo-a", nameof(IDashboardClient.HistoricalStats), "path", "silo-a")]
    [InlineData("/SiloProperties/{*address}", "/SiloProperties/silo-b", nameof(IDashboardClient.SiloProperties), "address", "silo-b")]
    [InlineData("/SiloMetadata/{*address}", "/SiloMetadata/silo-c", nameof(IDashboardClient.SiloMetadata), "address", "silo-c")]
    [InlineData("/SiloStats/{*address}", "/SiloStats/silo-d", nameof(IDashboardClient.SiloStats), "address", "silo-d")]
    [InlineData("/SiloCounters/{*address}", "/SiloCounters/silo-e", nameof(IDashboardClient.GetCounters), "address", "silo-e")]
    [InlineData("/GrainStats/{*grainName}", "/GrainStats/Acme.Customer", nameof(IDashboardClient.GrainStats), "grainName", "Acme.Customer")]
    [InlineData("/TopGrainMethods", "/TopGrainMethods", nameof(IDashboardClient.TopGrainMethods), null, null)]
    [InlineData("/GrainState", "/GrainState", nameof(IDashboardClient.GetGrainState), null, null)]
    [InlineData("/LifecycleStages", "/LifecycleStages", nameof(IDashboardClient.GetLifecycleStages), null, null)]
    [InlineData("/GrainTypes", "/GrainTypes", nameof(IDashboardClient.GetGrainTypes), null, null)]
    public async Task MapOrleansDashboard_SiloUnavailableFromEachApiEndpoint_ReturnsExact503(
        string routePattern,
        string requestPath,
        string expectedOperation,
        string? routeParameter,
        string? routeValue)
    {
        var client = new TestDashboardClient
        {
            ApiException = new SiloUnavailableException("cluster is unavailable"),
        };
        using var dashboard = DashboardRoutes.Create(client: client);
        var routeValues = routeParameter is null
            ? null
            : new Dictionary<string, object?> { [routeParameter] = routeValue };

        var response = await dashboard.ExecuteAsync(
            routePattern,
            requestPath,
            routeValues: routeValues,
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/plain", response.ContentType);
        Assert.Equal(
            "The dashboard has lost connectivity with the Orleans cluster",
            response.BodyText);
        Assert.Equal(expectedOperation, client.LastOperation);
    }

    [Fact]
    public async Task MapOrleansDashboard_ReminderLookupFails_ReturnsExactEmptyFallbackPayload()
    {
        var client = new TestDashboardClient
        {
            ReminderException = new InvalidOperationException("No reminder service is configured."),
        };
        using var dashboard = DashboardRoutes.Create(client: client);

        var response = await dashboard.ExecuteAsync(
            "/Reminders",
            "/Reminders",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("reminders").EnumerateArray());
        Assert.Equal(1, client.ReminderPage);
        Assert.Equal(50, client.ReminderPageSize);
        Assert.Equal(1, client.ReminderCalls);
    }

    [Fact]
    public async Task MapOrleansDashboard_HideTraceEnabled_Returns403ProblemDetails()
    {
        using var dashboard = DashboardRoutes.Create(hideTrace: true);

        var response = await dashboard.ExecuteAsync(
            "/Trace",
            "/Trace",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("type").GetString()));
        Assert.Equal("Trace Endpoint Disabled", root.GetProperty("title").GetString());
        Assert.Equal(403, root.GetProperty("status").GetInt32());
        Assert.Equal(
            "The trace endpoint is disabled in the dashboard options.",
            root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MapOrleansDashboard_HideTraceDisabled_ReturnsBannerAndHonorsRequestCancellation()
    {
        using var dashboard = DashboardRoutes.Create(hideTrace: false);
        using var cancellation = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var response = await dashboard.ExecuteAsync(
            "/Trace",
            "/Trace",
            requestAborted: cancellation.Token);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.StartsWith("   ____", response.BodyText, StringComparison.Ordinal);
        Assert.Contains(
            "You are connected to the Orleans Dashboard log streaming service",
            response.BodyText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Disconnecting after 60 minutes", response.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddDashboard_ClientBuilder_MappedEndpoint_ForwardsThroughDashboardClientToDashboardGrain()
    {
        string[]? forwardedExclusions = null;
        CancellationToken forwardedCancellationToken = default;
        var dashboardGrain = CreateProxy<IDashboardGrain>((method, arguments) =>
        {
            if (method.Name == nameof(IDashboardGrain.GetCounters))
            {
                forwardedExclusions = Assert.IsType<string[]>(arguments![0]);
                forwardedCancellationToken = Assert.IsType<CancellationToken>(arguments[1]);
                return Task.FromResult(new DashboardCounters(2)
                {
                    TotalActiveHostCount = 3,
                    TotalActivationCount = 11,
                }.AsImmutable());
            }

            throw new NotSupportedException(method.Name);
        });
        var reminderGrain = CreateProxy<IDashboardRemindersGrain>((method, _) =>
            throw new NotSupportedException(method.Name));
        var grainFactory = CreateProxy<IGrainFactory>((method, _) =>
        {
            if (method.IsGenericMethod && method.Name == nameof(IGrainFactory.GetGrain))
            {
                return method.GetGenericArguments()[0] switch
                {
                    var type when type == typeof(IDashboardGrain) => dashboardGrain,
                    var type when type == typeof(IDashboardRemindersGrain) => reminderGrain,
                    _ => throw new NotSupportedException(method.ToString()),
                };
            }

            throw new NotSupportedException(method.Name);
        });
        var builder = new TestClientBuilder();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(grainFactory);
        builder.AddDashboard();
        using var dashboard = DashboardRoutes.CreateFromServices(builder.Services);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var response = await dashboard.ExecuteAsync(
            "/DashboardCounters",
            "/DashboardCounters",
            "?exclude=%20System%20&exclude=Application",
            requestAborted: requestCancellation.Token);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(new[] { "System.", "Application." }, forwardedExclusions);
        Assert.Equal(requestCancellation.Token, forwardedCancellationToken);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(3, json.RootElement.GetProperty("totalActiveHostCount").GetInt32());
        Assert.Equal(11, json.RootElement.GetProperty("totalActivationCount").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("totalActiveHostCountHistory").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("totalActivationCountHistory").GetArrayLength());
    }

    private static string[] ExpectedRoutePatterns(string prefix)
    {
        string P(string suffix) => prefix + suffix;
        return
        [
            P("/"),
            P("/ClusterStats"),
            P("/DashboardCounters"),
            P("/GrainState"),
            P("/GrainStats/{*grainName}"),
            P("/GrainTypes"),
            P("/HistoricalStats/{*path}"),
            P("/LifecycleStages"),
            P("/Reminders"),
            P("/Reminders/{page:int}"),
            P("/SiloCounters/{*address}"),
            P("/SiloMetadata/{*address}"),
            P("/SiloProperties/{*address}"),
            P("/SiloStats/{*address}"),
            P("/TopGrainMethods"),
            P("/Trace"),
            P("/favicon.ico"),
            P("/fonts/{**path}"),
            P("/img/{**path}"),
            P("/index.css"),
            P("/index.html"),
            P("/index.min.js"),
            P("/version"),
        ];
    }

    private static byte[] ReadEmbeddedAsset(string assetName)
    {
        using var stream = typeof(DashboardOptions).Assembly.GetManifestResourceStream(
            $"Orleans.Dashboard.wwwroot.{assetName}");
        Assert.NotNull(stream);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var result = DispatchProxy.Create<T, MethodDispatchProxy>();
        ((MethodDispatchProxy)(object)result).Handler = handler;
        return result;
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class DashboardRoutes : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        private DashboardRoutes(ServiceProvider serviceProvider, IReadOnlyList<RouteEndpoint> endpoints)
        {
            _serviceProvider = serviceProvider;
            Endpoints = endpoints;
            RoutePatterns = endpoints
                .Select(endpoint => endpoint.RoutePattern.RawText!)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        public IReadOnlyList<RouteEndpoint> Endpoints { get; }

        public string[] RoutePatterns { get; }

        public static DashboardRoutes Create(
            string? routePrefix = null,
            TestDashboardClient? client = null,
            bool hideTrace = false,
            bool requireAuthorization = false)
        {
            var services = new ServiceCollection();
            services.AddRouting();
            services.AddLogging();
            services.AddSingleton<IDashboardClient>(client ?? new TestDashboardClient());
            services.AddSingleton<EmbeddedAssetProvider>();
            services.AddSingleton<DashboardLogger>();
            services.Configure<DashboardOptions>(options => options.HideTrace = hideTrace);

            return CreateFromServices(services, routePrefix, requireAuthorization);
        }

        public static DashboardRoutes CreateFromServices(
            IServiceCollection services,
            string? routePrefix = null,
            bool requireAuthorization = false)
        {
            services.AddLogging();
            services.TryAddSingleton<EmbeddedAssetProvider>();
            services.TryAddSingleton<DashboardLogger>();
            if (requireAuthorization)
            {
                AddAuthorizationServices(services);
            }

            var provider = services.BuildServiceProvider();
            var routeBuilder = new TestEndpointRouteBuilder(provider);
            var group = routeBuilder.MapOrleansDashboard(routePrefix);
            if (requireAuthorization)
            {
                group.RequireAuthorization(AuthorizationPolicy);
            }

            var endpoints = routeBuilder.DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();
            return new DashboardRoutes(provider, endpoints);
        }

        public Task<TestResponse> ExecuteAsync(
            string routePattern,
            string requestPath,
            string queryString = "",
            IReadOnlyDictionary<string, object?>? routeValues = null,
            string pathBase = "",
            System.Threading.CancellationToken requestAborted = default,
            string? ifNoneMatch = null) =>
            ExecuteCoreAsync(
                GetEndpoint(routePattern),
                requestPath,
                queryString,
                routeValues,
                pathBase,
                requestAborted,
                authenticated: null,
                ifNoneMatch);

        public Task<TestResponse> ExecuteAuthorizedAsync(
            string routePattern,
            string requestPath,
            bool authenticated,
            System.Threading.CancellationToken requestAborted = default) =>
            ExecuteCoreAsync(
                GetEndpoint(routePattern),
                requestPath,
                "",
                null,
                "",
                requestAborted,
                authenticated);

        private async Task<TestResponse> ExecuteCoreAsync(
            RouteEndpoint endpoint,
            string requestPath,
            string queryString,
            IReadOnlyDictionary<string, object?>? routeValues,
            string pathBase,
            System.Threading.CancellationToken requestAborted,
            bool? authenticated,
            string? ifNoneMatch = null)
        {
            using var requestScope = _serviceProvider.CreateScope();
            var context = new DefaultHttpContext
            {
                RequestServices = requestScope.ServiceProvider,
            };
            context.Request.Path = requestPath;
            context.Request.PathBase = pathBase;
            context.Request.QueryString = new QueryString(queryString);
            context.RequestAborted = requestAborted;
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
            context.Response.Body = new MemoryStream();
            if (routeValues is not null)
            {
                foreach (var pair in routeValues)
                {
                    context.Request.RouteValues[pair.Key] = pair.Value;
                }
            }

            if (authenticated is not null)
            {
                context.SetEndpoint(endpoint);
                if (authenticated.Value)
                {
                    context.Request.Headers["X-Test-User"] = "dashboard-user";
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "dashboard-user")],
                        TestAuthenticationHandler.SchemeName));
                }

                var middleware = new AuthorizationMiddleware(
                    endpoint.RequestDelegate!,
                    requestScope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>());
                await middleware.Invoke(context);
            }
            else
            {
                await endpoint.RequestDelegate!(context);
            }

            var responseStream = (MemoryStream)context.Response.Body;
            return new TestResponse(
                context.Response.StatusCode,
                context.Response.ContentType,
                context.Response.ContentLength,
                new HeaderDictionary(context.Response.Headers.ToDictionary()),
                responseStream.ToArray());
        }

        private RouteEndpoint GetEndpoint(string routePattern) =>
            Assert.Single(
                Endpoints,
                endpoint => string.Equals(endpoint.RoutePattern.RawText, routePattern, StringComparison.Ordinal));

        public void Dispose() => _serviceProvider.Dispose();

        private static void AddAuthorizationServices(IServiceCollection services)
        {
            services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    _ => { });
            services.AddAuthorization(options =>
                options.AddPolicy(AuthorizationPolicy, policy => policy.RequireAuthenticatedUser()));
        }
    }

    private sealed record TestResponse(
        int StatusCode,
        string? ContentType,
        long? ContentLength,
        IHeaderDictionary Headers,
        byte[] Body)
    {
        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "DashboardTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var userName))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userName.ToString())],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TestDashboardClient : IDashboardClient
    {
        public DashboardCounters DashboardCountersResult { get; set; } = new(2)
        {
            TotalActiveHostCount = 2,
            TotalActivationCount = 8,
        };

        public Dictionary<string, GrainTraceEntry> ClusterStatsResult { get; set; } = [];

        public ReminderResponse ReminderResult { get; set; } = new()
        {
            Count = 0,
            Reminders = [],
        };

        public string GrainStateResult { get; set; } = "{}";

        public Exception? ClusterStatsException { get; set; }

        public Exception? ReminderException { get; set; }

        public Exception? ApiException { get; set; }

        public string? LastOperation { get; private set; }

        public string? LastArgument { get; private set; }

        public int DashboardCountersCalls { get; private set; }

        public int ClusterStatsCalls { get; private set; }

        public int ReminderCalls { get; private set; }

        public int TopGrainMethodsCalls { get; private set; }

        public int GrainStateCalls { get; private set; }

        public int GrainTypesCalls { get; private set; }

        public int TotalCalls =>
            DashboardCountersCalls
            + ClusterStatsCalls
            + ReminderCalls
            + TopGrainMethodsCalls
            + GrainStateCalls
            + GrainTypesCalls;

        public string[] DashboardCounterExclusions { get; private set; } = [];

        public string[] TopMethodExclusions { get; private set; } = [];

        public int TopMethodTake { get; private set; }

        public string[] GrainTypeExclusions { get; private set; } = [];

        public string? GrainStateId { get; private set; }

        public string? GrainStateType { get; private set; }

        public int ReminderPage { get; private set; }

        public int ReminderPageSize { get; private set; }

        public Task<Immutable<DashboardCounters>> DashboardCounters(
            string[]? exclusions = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DashboardCountersCalls++;
            DashboardCounterExclusions = exclusions ?? [];
            return Complete(nameof(DashboardCounters), DashboardCountersResult.AsImmutable());
        }

        public Task<Immutable<Dictionary<string, GrainTraceEntry>>> ClusterStats(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClusterStatsCalls++;
            return Complete(
                nameof(ClusterStats),
                ClusterStatsResult.AsImmutable(),
                exception: ClusterStatsException);
        }

        public Task<Immutable<ReminderResponse>> GetReminders(
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReminderCalls++;
            ReminderPage = pageNumber;
            ReminderPageSize = pageSize;
            return Complete(
                nameof(GetReminders),
                ReminderResult.AsImmutable(),
                exception: ReminderException);
        }

        public Task<Immutable<SiloRuntimeStatistics?[]>> HistoricalStats(
            string siloAddress,
            CancellationToken cancellationToken = default) =>
            Complete(
                nameof(HistoricalStats),
                Array.Empty<SiloRuntimeStatistics?>().AsImmutable(),
                siloAddress,
                cancellationToken: cancellationToken);

        public Task<Immutable<Dictionary<string, string?>>> SiloProperties(
            string siloAddress,
            CancellationToken cancellationToken = default) =>
            Complete(
                nameof(SiloProperties),
                new Dictionary<string, string?>().AsImmutable(),
                siloAddress,
                cancellationToken: cancellationToken);

        public Task<Immutable<Dictionary<string, string>>> SiloMetadata(
            string siloAddress,
            CancellationToken cancellationToken = default) =>
            Complete(
                nameof(SiloMetadata),
                new Dictionary<string, string>().AsImmutable(),
                siloAddress,
                cancellationToken: cancellationToken);

        public Task<Immutable<Dictionary<string, GrainTraceEntry>>> SiloStats(
            string siloAddress,
            CancellationToken cancellationToken = default) =>
            Complete(
                nameof(SiloStats),
                new Dictionary<string, GrainTraceEntry>().AsImmutable(),
                siloAddress,
                cancellationToken: cancellationToken);

        public Task<Immutable<StatCounter[]>> GetCounters(
            string siloAddress,
            CancellationToken cancellationToken = default) =>
            Complete(
                nameof(GetCounters),
                Array.Empty<StatCounter>().AsImmutable(),
                siloAddress,
                cancellationToken: cancellationToken);

        public Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GrainStats(
            string grainName,
            CancellationToken cancellationToken = default) =>
            Complete(
                nameof(GrainStats),
                new Dictionary<string, Dictionary<string, GrainTraceEntry>>().AsImmutable(),
                grainName,
                cancellationToken: cancellationToken);

        public Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(
            int take,
            string[]? exclusions = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TopGrainMethodsCalls++;
            TopMethodTake = take;
            TopMethodExclusions = exclusions ?? [];
            return Complete(
                nameof(TopGrainMethods),
                new Dictionary<string, GrainMethodAggregate[]>().AsImmutable());
        }

        public Task<Immutable<string>> GetGrainState(
            string? id,
            string? grainType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GrainStateCalls++;
            GrainStateId = id;
            GrainStateType = grainType;
            return Complete(nameof(GetGrainState), GrainStateResult.AsImmutable(), id);
        }

        public Task<Immutable<string[]>> GetGrainTypes(
            string[]? exclusions = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GrainTypesCalls++;
            GrainTypeExclusions = exclusions ?? [];
            return Complete(nameof(GetGrainTypes), Array.Empty<string>().AsImmutable());
        }

        public Task<Immutable<LifecycleStageInfo[]>> GetLifecycleStages(
            CancellationToken cancellationToken = default) =>
            Complete(
                nameof(GetLifecycleStages),
                Array.Empty<LifecycleStageInfo>().AsImmutable(),
                cancellationToken: cancellationToken);

        private Task<T> Complete<T>(
            string operation,
            T result,
            string? argument = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOperation = operation;
            LastArgument = argument;
            var failure = exception ?? ApiException;
            return failure is null ? Task.FromResult(result) : Task.FromException<T>(failure);
        }
    }

    private class MethodDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } =
            static (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);
    }
}
