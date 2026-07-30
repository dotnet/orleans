using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Interceptors;

public static class OutgoingFilterRegistration
{
    public static void ConfigureDelegateFilter(ISiloBuilder builder)
    {
        // <outgoing_delegate_filter>
        builder.AddOutgoingGrainCallFilter(async context =>
        {
            // If the method being called is 'MyInterceptedMethod', then set a value
            // on the RequestContext which can then be read by other filters or the grain.
            if (string.Equals(
                context.InterfaceMethod.Name,
                nameof(IMyGrain.MyInterceptedMethod)))
            {
                RequestContext.Set(
                    "intercepted value", "this value was added by the filter");
            }

            await context.Invoke();

            // If the grain method returned an int, set the result to double that value.
            if (context.Result is int resultValue)
            {
                context.Result = resultValue * 2;
            }
        });
        // </outgoing_delegate_filter>
    }

    public static void RegisterLoggingFilter(ISiloBuilder builder)
    {
        // <register_outgoing_logging_filter>
        builder.AddOutgoingGrainCallFilter<OutgoingLoggingCallFilter>();
        // </register_outgoing_logging_filter>
    }

    public static void RegisterFilterWithDI(ISiloBuilder builder)
    {
        // <register_outgoing_filter_di>
        builder.Services
            .AddSingleton<IOutgoingGrainCallFilter, OutgoingLoggingCallFilter>();
        // </register_outgoing_filter_di>
    }
}
