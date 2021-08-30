using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        private static readonly ActivitySource activitySource = new(ActivitySourceName);

        protected static async Task ProcessNewActivity(IGrainCallContext context, string activityName, ActivityKind activityKind, ActivityContext activityContext)
        {
            // rpc attributes from https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/rpc.md
            ActivityTagsCollection tags = null;
            if (activitySource.HasListeners())
            {
                tags = new ActivityTagsCollection
                {
                    {"rpc.service", context.InterfaceMethod?.DeclaringType?.FullName},
                    {"rpc.method", context.InterfaceMethod?.Name},
                    {"net.peer.name", context.Grain?.ToString()},
                    {"rpc.system", "orleans"}
                };
            }

            using var activity = activitySource.StartActivity(activityName, activityKind, activityContext, tags);
            if (activity is not null)
            {
                RequestContext.Set(TraceParentHeaderName, activity.Id);
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
        }
    }

    internal class ActivityPropagationOutgoingGrainCallFilter : ActivityPropagationGrainCallFilter, IOutgoingGrainCallFilter
    {
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (Activity.Current != null)
            {
                return ProcessCurrentActivity(context); // Copy existing activity to RequestContext
            }
            return ProcessNewActivity(context, ActivityNameOut, ActivityKind.Client, new ActivityContext()); // Create new activity
        }

        private static Task ProcessCurrentActivity(IOutgoingGrainCallContext context)
        {
            var currentActivity = Activity.Current;

            if (currentActivity is not null &&
                currentActivity.IdFormat == ActivityIdFormat.W3C)
            {
                RequestContext.Set(TraceParentHeaderName, currentActivity.Id);
                if (currentActivity.TraceStateString is not null)
                    RequestContext.Set(TraceStateHeaderName, currentActivity.TraceStateString);
            }

            return context.Invoke();
        }
    }

    internal class ActivityPropagationIncomingGrainCallFilter : ActivityPropagationGrainCallFilter, IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context)
        {
            // Create activity from context directly
            var traceParent = RequestContext.Get(TraceParentHeaderName) as string;
            var traceState = RequestContext.Get(TraceStateHeaderName) as string;
            var parentContext = new ActivityContext();

            if (traceParent is not null)
            {
                parentContext = ActivityContext.Parse(traceParent, traceState);
            }
            return ProcessNewActivity(context, ActivityNameIn, ActivityKind.Server, parentContext);
        }
    }
}
