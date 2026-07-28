using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Interceptors;

// <grain_factory_injection>
public class CustomCallFilter : Orleans.IIncomingGrainCallFilter
{
    private readonly IGrainFactory _grainFactory;

    public CustomCallFilter(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task Invoke(Orleans.IIncomingGrainCallContext context)
    {
        // Hook calls to any grain other than ICustomFilterGrain implementations.
        // This avoids potential infinite recursion when calling OnReceivedCall() below.
        if (context.Grain is not ICustomFilterGrain)
        {
            var filterGrain = _grainFactory.GetGrain<ICustomFilterGrain>(
                ((IAddressable)context.Grain).GetPrimaryKeyLong());

            // Perform some grain call here.
            await filterGrain.OnReceivedCall();
        }

        // Continue invoking the call on the target grain.
        await context.Invoke();
    }
}
// </grain_factory_injection>
