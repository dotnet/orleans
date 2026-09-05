using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Interceptors;

public static class IncomingFilterConfiguration
{
    public static void ConfigureDelegateFilter(ISiloBuilder siloHostBuilder)
    {
        // <silo_delegate_filter>
        siloHostBuilder.AddIncomingGrainCallFilter(async context =>
        {
            // If the method being called is 'MyInterceptedMethod', then set a value
            // on the RequestContext which can then be read by other filters or the grain.
            if (string.Equals(
                context.InterfaceMethod.Name,
                nameof(IMyGrain.MyInterceptedMethod),
                StringComparison.Ordinal))
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
        // </silo_delegate_filter>
    }

    public static void RegisterLoggingFilter(ISiloBuilder siloHostBuilder)
    {
        // <register_incoming_filter>
        siloHostBuilder.AddIncomingGrainCallFilter<LoggingCallFilter>();
        // </register_incoming_filter>
    }

    public static void RegisterGrainFactoryFilter(ISiloBuilder siloHostBuilder)
    {
        // <register_grain_factory_filter>
        siloHostBuilder.AddIncomingGrainCallFilter<AuditCallFilter>();
        // </register_grain_factory_filter>
    }
}

public static class OutgoingFilterConfiguration
{
    public static void RegisterLoggingFilter(ISiloBuilder builder)
    {
        // <register_outgoing_filter>
        builder.AddOutgoingGrainCallFilter<OutgoingLoggingCallFilter>();
        // </register_outgoing_filter>
    }
}
