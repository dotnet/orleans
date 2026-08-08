namespace Orleans.Docs.Snippets.Interceptors;

// <grain_factory_injection>
public sealed class AuditCallFilter(IGrainFactory grainFactory)
    : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // Exclude the audit interface so its call doesn't reenter this filter recursively.
        if (context.InterfaceMethod.DeclaringType != typeof(ICallAuditGrain))
        {
            var auditGrain = grainFactory.GetGrain<ICallAuditGrain>(
                context.TargetId.ToString());
            await auditGrain.RecordCallAttempt(
                context.InterfaceName,
                context.MethodName);
        }

        await context.Invoke();
    }
}
// </grain_factory_injection>
