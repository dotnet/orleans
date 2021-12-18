using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal abstract class ActivityPropagationGrainCallFilter
    {
        protected const string TraceParentHeaderName = "traceparent";
        protected const string TraceStateHeaderName = "tracestate";

        internal const string ActivitySourceName = "orleans.runtime.graincall";
        internal const string ActivityNameIn = "Orleans.Runtime.GrainCall.In";
        internal const string ActivityNameOut = "Orleans.Runtime.GrainCall.Out";

        protected static readonly ActivitySource activitySource = new ActivitySource(ActivitySourceName);

        protected static async Task Process(IGrainCallContext context, Activity activity)
        {
            if (activity is not null)
            {
                // rpc attributes from https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/rpc.md
                activity.SetTag("rpc.service", context.InterfaceMethod?.DeclaringType?.FullName);
                activity.SetTag("rpc.method", context.InterfaceMethod?.Name);
                activity.SetTag("net.peer.name", context.Grain?.ToString());
                activity.SetTag("rpc.system", "orleans");
            }

            try
            {
                await context.Invoke();
                if (activity is not null && activity.IsAllDataRequested)
                {
                    activity.SetTag("status", "Ok");
                }
            }
            catch (Exception e)
            {
                if (activity is not null && activity.IsAllDataRequested)
                {
                    // exception attributes from https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/exceptions.md
                    activity.SetTag("exception.type", e.GetType().FullName);
                    activity.SetTag("exception.message", e.Message);
                    activity.SetTag("exception.stacktrace", e.StackTrace);
                    activity.SetTag("exception.escaped", true);
                    activity.SetTag("status", "Error");
                }
                throw;
            }
            finally
            {
                activity?.Stop();
            }
        }
    }

    internal class ActivityPropagationOutgoingGrainCallFilter : ActivityPropagationGrainCallFilter, IOutgoingGrainCallFilter
    {
        private readonly DistributedContextPropagator propagator;

        public ActivityPropagationOutgoingGrainCallFilter(DistributedContextPropagator propagator)
        {
            this.propagator = propagator;
        }

        public Task Invoke(IOutgoingGrainCallContext context)
        {
            var activity = activitySource.StartActivity(ActivityNameOut, ActivityKind.Client);

            if (activity != null)
            {
                propagator.Inject(activity, null, static (carrier, key, value) =>
                {
                    RequestContext.Set(key, value);
                });
            }

            return Process(context, activity);
        }

    }

    internal class ActivityPropagationIncomingGrainCallFilter : ActivityPropagationGrainCallFilter, IIncomingGrainCallFilter
    {
        private readonly DistributedContextPropagator propagator;

        public ActivityPropagationIncomingGrainCallFilter(DistributedContextPropagator propagator)
        {
            this.propagator = propagator;
        }

        public Task Invoke(IIncomingGrainCallContext context)
        {
            var activity = activitySource.CreateActivity(ActivityNameIn, ActivityKind.Server);
            if (activity is not null)
            {
                propagator.ExtractTraceIdAndState(null,
                    static (object carrier, string fieldName, out string fieldValue, out IEnumerable<string> fieldValues) =>
                    {
                        fieldValues = default;
                        fieldValue = RequestContext.Get(fieldName) as string;
                    },
                    out var traceParent,
                    out var traceState);
                if (!string.IsNullOrEmpty(traceParent))
                {
                    activity.SetParentId(traceParent);
                    if (!string.IsNullOrEmpty(traceState))
                    {
                        activity.TraceStateString = traceState;
                    }
                    var baggage = propagator.ExtractBaggage(null, static (object carrier, string fieldName, out string fieldValue, out IEnumerable<string> fieldValues) =>
                    {
                        fieldValues = default;
                        fieldValue = RequestContext.Get(fieldName) as string;
                    });
                    if (baggage is not null)
                    {
                        foreach (var baggageItem in baggage)
                        {
                            activity.AddBaggage(baggageItem.Key, baggageItem.Value);
                        }
                    }
                }
                activity.Start();
            }
            return Process(context, activity);
        }
    }
}
