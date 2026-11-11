using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Orleans.Messaging.Tests.Support;

internal static class MessagingTestGrainTypes
{
    // The source-linked playground and recipe payment grains share the same production grain type.
    public static void UseDocumentationPaymentGrain(this IServiceCollection services) =>
        services.PostConfigure<GrainTypeOptions>(options =>
            options.Classes.Remove(typeof(OrderProcessing.Grains.PaymentGrain)));
}
