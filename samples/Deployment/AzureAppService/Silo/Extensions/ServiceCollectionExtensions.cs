// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Extensions.DependencyInjection;

internal static class ServiceCollectionExtensions
{
    internal static void AddApplicationInsights(
        this IServiceCollection services, string applicationName)
    {
        services.AddApplicationInsightsTelemetry();
        services.AddSingleton<ITelemetryInitializer>(
            _ => new ApplicationMapNodeNameInitializer(applicationName));
    }
}
