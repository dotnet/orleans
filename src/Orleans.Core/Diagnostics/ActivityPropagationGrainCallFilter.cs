using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal static class ActivityPropagationGrainCallFilter
    {
        private const string TraceParentHeaderName = "traceparent";
        private const string TraceStateHeaderName = "tracestate";
        private const string TraceBaggageHeaderName = "tracebaggage";
        
        internal const string DiagnosticListenerName = "Orleans.Runtime.GrainCall";
        internal const string ActivityNameIn = "Orleans.Runtime.GrainCall.In";
        internal const string ActivityStartNameIn = "Orleans.Runtime.GrainCall.In.Start";
        internal const string ExceptionEventNameIn = "Orleans.Runtime.GrainCall.In.Exception";

        internal const string ActivityNameOut = "Orleans.Runtime.GrainCall.Out";
        internal const string ActivityStartNameOut = "Orleans.Runtime.GrainCall.Out.Start";
        internal const string ExceptionEventNameOut = "Orleans.Runtime.GrainCall.Out.Exception";

        private static readonly DiagnosticListener DiagnosticListener = new DiagnosticListener(DiagnosticListenerName);
        
        public class ActivityPropagationOutgoingGrainCallFilter : IOutgoingGrainCallFilter
        {
            public Task Invoke(IOutgoingGrainCallContext context)
            {
                if (Activity.Current != null)
                    return ProcessCurrentActivity(context); // Copy existing activity to RequestContext

                if (DiagnosticListener.IsEnabled(ActivityNameOut, context))
                    return ProcessDiagnosticSource(context); //Create activity using DiagnosticListener

                return ProcessNewActivity(context); // Create activity directly
            }

            private static Task ProcessCurrentActivity(IOutgoingGrainCallContext context)
            {
                var currentActivity = Activity.Current;

                if (currentActivity != null &&
                    currentActivity.IdFormat == ActivityIdFormat.W3C)
                {
                    RequestContext.Set(TraceParentHeaderName, currentActivity.Id);
                    if (Activity.Current.TraceStateString != null)
                        RequestContext.Set(TraceStateHeaderName, currentActivity.TraceStateString);

                    using var e = currentActivity.Baggage.GetEnumerator();
                    if (e.MoveNext())
                    {
                        var baggage = new List<KeyValuePair<string, string>>();

                        do
                        {
                            baggage.Add(e.Current);
                        }
                        while (e.MoveNext());

                        baggage.TrimExcess();
                        RequestContext.Set(TraceBaggageHeaderName, baggage);
                    }
                }

                return context.Invoke();
            }

            private async Task ProcessDiagnosticSource(IOutgoingGrainCallContext context)
            {
                var activity = new Activity(ActivityNameOut);
                // Only send start event to users who subscribed for it, but start activity anyway
                if (DiagnosticListener.IsEnabled(ActivityStartNameOut))
                {
                    DiagnosticListener.StartActivity(activity, new ActivityStartData(context));
                }
                else
                {
                    activity.Start();
                }
                RequestContext.Set(TraceParentHeaderName, activity.Id);
                try
                {
                    await context.Invoke();
                }
                catch (Exception ex)
                {
                    if (DiagnosticListener.IsEnabled(ExceptionEventNameOut))
                    {
                        // If request was initially instrumented, Activity.Current has all necessary context for logging
                        // Request is passed to provide some context if instrumentation was disabled and to avoid
                        // extensive Activity.Tags usage to tunnel request properties
                        DiagnosticListener.Write(ExceptionEventNameOut, new ExceptionData(ex, context));
                    }
                    throw;
                }
                finally
                {
                    DiagnosticListener.StopActivity(activity, new ActivityStopData(context));
                }
            }

            private static async Task ProcessNewActivity(IOutgoingGrainCallContext context)
            {
                var activity = new Activity(ActivityNameOut);
                activity.Start();
                RequestContext.Set(TraceParentHeaderName, activity.Id);

                try
                {
                    await context.Invoke();
                }
                finally
                {
                    activity.Stop();
                }
            }

            private sealed class ActivityStartData
            {
                internal ActivityStartData(IOutgoingGrainCallContext context) => Context = context;

                public IOutgoingGrainCallContext Context { get; }

                public override string ToString() => $"{{ {nameof(Context)} = {Context.InterfaceMethod} }}";
            }

            private sealed class ActivityStopData
            {
                internal ActivityStopData(IOutgoingGrainCallContext context) => Context = context;

                public IOutgoingGrainCallContext Context { get; }

                public override string ToString() => $"{{ {nameof(Context)} = {Context.InterfaceMethod} }}";
            }

            private sealed class ExceptionData
            {
                internal ExceptionData(Exception exception, IOutgoingGrainCallContext context)
                {
                    Exception = exception;
                    Context = context;
                }

                public Exception Exception { get; }
                public IOutgoingGrainCallContext Context { get; }

                public override string ToString() => $"{{ {nameof(Exception)} = {Exception}, {nameof(Context)} = {Context.InterfaceMethod} }}";
            }
        }

        public class ActivityPropagationIncomingGrainCallFilter : IIncomingGrainCallFilter
        {
            public Task Invoke(IIncomingGrainCallContext context)
            {
                if (DiagnosticListener.IsEnabled(ActivityNameIn, context))
                    return ProcessDiagnosticSource(context); //Create activity from context using DiagnosticListener

                return ProcessActivity(context); // Create activity from context directly
            }

            private async Task ProcessDiagnosticSource(IIncomingGrainCallContext context)
            {
                var activity = CreateActivity();
                // Only send start event to users who subscribed for it, but start activity anyway
                if (DiagnosticListener.IsEnabled(ActivityStartNameIn))
                {
                    DiagnosticListener.StartActivity(activity, new ActivityStartData(context));
                }
                else
                {
                    activity.Start();
                }
                RequestContext.Set(TraceParentHeaderName, activity.Id);
                try
                {
                    await context.Invoke();
                }
                catch (Exception ex)
                {
                    if (DiagnosticListener.IsEnabled(ExceptionEventNameIn))
                    {
                        // If request was initially instrumented, Activity.Current has all necessary context for logging
                        // Request is passed to provide some context if instrumentation was disabled and to avoid
                        // extensive Activity.Tags usage to tunnel request properties
                        DiagnosticListener.Write(ExceptionEventNameIn, new ExceptionData(ex, context));
                    }
                    throw;
                }
                finally
                {
                    DiagnosticListener.StopActivity(activity, new ActivityStopData(context));
                }
            }

            private static async Task ProcessActivity(IIncomingGrainCallContext context)
            {
                var activity = CreateActivity();
                activity.Start();
                RequestContext.Set(TraceParentHeaderName, activity.Id);

                try
                {
                    await context.Invoke();
                }
                finally
                {
                    activity.Stop();
                }
            }

            private static Activity CreateActivity()
            {
                var currentActivity = new Activity(ActivityNameIn);
                currentActivity.TraceStateString = (string)RequestContext.Get(TraceStateHeaderName);
                currentActivity.SetParentId((string)RequestContext.Get(TraceParentHeaderName));

                if (RequestContext.Get(TraceBaggageHeaderName) is List<KeyValuePair<string, string>> baggage)
                {
                    foreach (var pair in baggage)
                        currentActivity.AddBaggage(pair.Key, pair.Value);
                }

                return currentActivity;
            }

            private sealed class ActivityStartData
            {
                internal ActivityStartData(IIncomingGrainCallContext context) => Context = context;

                public IIncomingGrainCallContext Context { get; }

                public override string ToString() => $"{{ {nameof(Context)} = {Context.ImplementationMethod ?? Context.InterfaceMethod} }}";
            }

            private sealed class ActivityStopData
            {
                internal ActivityStopData(IIncomingGrainCallContext context) => Context = context;

                public IIncomingGrainCallContext Context { get; }

                public override string ToString() => $"{{ {nameof(Context)} = {Context.ImplementationMethod ?? Context.InterfaceMethod} }}";
            }

            private sealed class ExceptionData
            {
                internal ExceptionData(Exception exception, IIncomingGrainCallContext context)
                {
                    Exception = exception;
                    Context = context;
                }

                public Exception Exception { get; }
                public IIncomingGrainCallContext Context { get; }

                public override string ToString() => $"{{ {nameof(Exception)} = {Exception}, {nameof(Context)} = {Context.ImplementationMethod ?? Context.InterfaceMethod} }}";
            }
        }
    }
}
