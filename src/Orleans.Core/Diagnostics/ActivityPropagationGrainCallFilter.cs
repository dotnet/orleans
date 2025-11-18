using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// A grain call filter which helps to propagate activity correlation information across a call chain.
    /// </summary>
    internal abstract class ActivityPropagationGrainCallFilter
    {
        protected const string TraceParentHeaderName = "traceparent";
        protected const string TraceStateHeaderName = "tracestate";

        internal const string RpcSystem = "orleans";
        internal const string OrleansNamespacePrefix = "Orleans";
        internal const string ApplicationGrainActivitySourceName = "Microsoft.Orleans.Application";
        internal const string RuntimeActivitySourceName = "Microsoft.Orleans.Runtime";

        protected static readonly ActivitySource ApplicationGrainSource = new(ApplicationGrainActivitySourceName, "1.0.0");
        protected static readonly ActivitySource RuntimeGrainSource = new(RuntimeActivitySourceName, "1.0.0");

        protected static ActivitySource GetActivitySource(IGrainCallContext context) =>
            context.Request.GetInterfaceType().Namespace?.StartsWith(OrleansNamespacePrefix) == true
                ? RuntimeGrainSource
                : ApplicationGrainSource;

        protected static void GetRequestContextValue(object carrier, string fieldName, out string fieldValue, out IEnumerable<string> fieldValues)
        {
            fieldValues = default;
            fieldValue = RequestContext.Get(fieldName) as string;
        }

        protected static async Task Process(IGrainCallContext context, Activity activity)
        {
            if (activity is not null)
            {
                // rpc attributes from https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/rpc.md
                activity.SetTag("rpc.system", RpcSystem);
                activity.SetTag("rpc.service", context.InterfaceName);
                activity.SetTag("rpc.method", context.MethodName);

                if (activity.IsAllDataRequested)
                {
                    // Custom attributes
                    activity.SetTag("rpc.orleans.target_id", context.TargetId.ToString());
                    if (context.SourceId is GrainId sourceId)
                    {
                        activity.SetTag("rpc.orleans.source_id", sourceId.ToString());
                    }
                }
            }

            try
            {
                await context.Invoke();
            }
            catch (Exception e)
            {
                if (activity is not null && activity.IsAllDataRequested)
                {
                    activity.SetStatus(ActivityStatusCode.Error);

                    // exception attributes from https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/exceptions.md
                    activity.SetTag("exception.type", e.GetType().FullName);
                    activity.SetTag("exception.message", e.Message);

                    // Note that "exception.stacktrace" is the full exception detail, not just the StackTrace property.
                    // See https://opentelemetry.io/docs/specs/semconv/attributes-registry/exception/
                    // and https://github.com/open-telemetry/opentelemetry-specification/pull/697#discussion_r453662519
                    activity.SetTag("exception.stacktrace", e.ToString());
                    activity.SetTag("exception.escaped", true);
                }

                throw;
            }
            finally
            {
                activity?.Stop();
            }
        }
    }

    /// <summary>
    /// Propagates distributed context information to outgoing requests.
    /// </summary>
    internal class ActivityPropagationOutgoingGrainCallFilter : ActivityPropagationGrainCallFilter, IOutgoingGrainCallFilter
    {
        private readonly DistributedContextPropagator _propagator;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivityPropagationOutgoingGrainCallFilter"/> class.
        /// </summary>
        /// <param name="propagator">The context propagator.</param>
        public ActivityPropagationOutgoingGrainCallFilter(DistributedContextPropagator propagator)
        {
            _propagator = propagator;
        }

        /// <inheritdoc />
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            var source = GetActivitySource(context);
            var activity = source.StartActivity(context.Request.GetActivityName(), ActivityKind.Client);

            if (activity is null)
            {
                return context.Invoke();
            }

            _propagator.Inject(activity, null, static (carrier, key, value) => RequestContext.Set(key, value));
            return Process(context, activity);
        }
    }

    /// <summary>
    /// Populates distributed context information from incoming requests.
    /// </summary>
    internal class ActivityPropagationIncomingGrainCallFilter : ActivityPropagationGrainCallFilter, IIncomingGrainCallFilter
    {
        private readonly DistributedContextPropagator _propagator;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivityPropagationIncomingGrainCallFilter"/> class.
        /// </summary>
        /// <param name="propagator">The context propagator.</param>
        public ActivityPropagationIncomingGrainCallFilter(DistributedContextPropagator propagator)
        {
            _propagator = propagator;
        }

        /// <inheritdoc />
        public Task Invoke(IIncomingGrainCallContext context)
        {
            Activity activity = default;
            _propagator.ExtractTraceIdAndState(null, GetRequestContextValue, out var traceParent, out var traceState);

            var source = GetActivitySource(context);
            if (!string.IsNullOrEmpty(traceParent))
            {
                if (ActivityContext.TryParse(traceParent, traceState, isRemote: true, out ActivityContext parentContext))
                {
                    // traceParent is a W3CId
                    activity = source.CreateActivity(context.Request.GetActivityName(), ActivityKind.Server, parentContext);
                }
                else
                {
                    // Most likely, traceParent uses ActivityIdFormat.Hierarchical
                    activity = source.CreateActivity(context.Request.GetActivityName(), ActivityKind.Server, traceParent);
                }

                if (activity is not null)
                {
                    if (!string.IsNullOrEmpty(traceState))
                    {
                        activity.TraceStateString = traceState;
                    }

                    var baggage = _propagator.ExtractBaggage(null, GetRequestContextValue);

                    if (baggage is not null)
                    {
                        foreach (var baggageItem in baggage)
                        {
                            activity.AddBaggage(baggageItem.Key, baggageItem.Value);
                        }
                    }
                }
            }
            else
            {
                activity = source.CreateActivity(context.Request.GetActivityName(), ActivityKind.Server);
            }

            if (activity is null)
            {
                return context.Invoke();
            }

            activity.Start();
            return Process(context, activity);
        }
    }
}
