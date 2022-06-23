using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class RequestContextTestGrain : Grain, IRequestContextTestGrain
    {
        public Task<string> TraceIdEcho()
        {
            return Task.FromResult(RequestContext.Get("TraceId") as string);
        }

        public Task<string> TraceIdDoubleEcho()
        {
            var grain = GrainFactory.GetGrain<IRequestContextTestGrain>((new Random()).Next());
            return grain.TraceIdEcho();
        }

        public Task<string> TraceIdDelayedEcho1()
        {
            return Task.Factory.StartNew(() => RequestContext.Get("TraceId") as string);
        }

        public async Task<string> TraceIdDelayedEcho2()
        {
            await Task.CompletedTask;
            return RequestContext.Get("TraceId") as string;
        }

        public Task<Guid> E2EActivityId()
        {
            return Task.FromResult(RequestContext.ActivityId);
        }

        public Task<Guid> E2ELegacyActivityId()
        {
            if (!RequestContext.PropagateActivityId) throw new InvalidOperationException("ActivityId propagation is not enabled on silo.");
            return Task.FromResult(Trace.CorrelationManager.ActivityId);
        }
    }

    public class RequestContextTaskGrain : Grain, IRequestContextTaskGrain
    {
        private ILogger logger;

        public RequestContextTaskGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public Task<string> TraceIdEcho()
        {
            string traceId = RequestContext.Get("TraceId") as string;
            logger.LogInformation("{Method}: TraceId={TraceId}", "TraceIdEcho", traceId);
            return Task.FromResult(traceId);
        }

        public Task<string> TraceIdDoubleEcho()
        {
            var grain = GrainFactory.GetGrain<IRequestContextTaskGrain>((new Random()).Next());
            return grain.TraceIdEcho();
        }

        public Task<string> TraceIdDelayedEcho1()
        {
            string method = "TraceIdDelayedEcho1";
            logger.LogInformation("{Method}: Entered", method);
            string traceIdOutside = RequestContext.Get("TraceId") as string;
            logger.LogInformation("{Method}: Outside TraceId={TraceId}", method, traceIdOutside);

            return Task.Factory.StartNew(() =>
            {
                string traceIdInside = RequestContext.Get("TraceId") as string;
                logger.LogInformation("{Method}: Inside TraceId={TraceId}", method, traceIdInside);
                return traceIdInside;
            });
        }

        public Task<string> TraceIdDelayedEcho2()
        {
            string method = "TraceIdDelayedEcho2";
            logger.LogInformation("{Method}: Entered", method);
            string traceIdOutside = RequestContext.Get("TraceId") as string;
            logger.LogInformation("{Method}: Outside TraceId={TraceId}", method, traceIdOutside);

            return Task.CompletedTask.ContinueWith(task =>
            {
                string traceIdInside = RequestContext.Get("TraceId") as string;
                logger.LogInformation("{Method}: Inside TraceId={TraceId}", method, traceIdInside);
                return traceIdInside;
            });
        }

        public async Task<string> TraceIdDelayedEchoAwait()
        {
            string method = "TraceIdDelayedEchoAwait";
            logger.LogInformation("{Method}: Entered", method);
            string traceIdOutside = RequestContext.Get("TraceId") as string;
            logger.LogInformation("{Method}: Outside TraceId={TraceId}", method, traceIdOutside);

            string traceId = await Task.CompletedTask.ContinueWith(task =>
            {
                string traceIdInside = RequestContext.Get("TraceId") as string;
                logger.LogInformation("{Method}: Inside TraceId={TraceId}", method, traceIdInside);
                return traceIdInside;
            });
            logger.LogInformation("{Method}: After await TraceId={TraceId}", "TraceIdDelayedEchoAwait", traceId);
            return traceId;
        }

        public Task<string> TraceIdDelayedEchoTaskRun()
        {
            string method = "TraceIdDelayedEchoTaskRun";
            logger.LogInformation("{Method}: Entered", method);
            string traceIdOutside = RequestContext.Get("TraceId") as string;
            logger.LogInformation("{Method}: Outside TraceId={TraceId}", method, traceIdOutside);

            return Task.Run(() =>
            {
                string traceIdInside = RequestContext.Get("TraceId") as string;
                logger.LogInformation("{Method}: Inside TraceId={TraceId}", method, traceIdInside);
                return traceIdInside;
            });
        }

        public Task<Guid> E2EActivityId()
        {
            return Task.FromResult(RequestContext.ActivityId);
        }

        public async Task<Tuple<string, string>> TestRequestContext()
        {
            string bar1 = null;
            RequestContext.Set("jarjar", "binks");

            Task task = Task.Factory.StartNew(() =>
            {
                bar1 = (string)RequestContext.Get("jarjar");
                logger.LogInformation("jarjar inside Task.Factory.StartNew = {Bar}.", bar1);
            });

            string bar2 = null;
            Task ac = Task.Factory.StartNew(() =>
            {
                bar2 = (string)RequestContext.Get("jarjar");
                logger.LogInformation("jarjar inside Task.StartNew  = {Bar}.", bar2);
            });

            await Task.WhenAll(task, ac);
            return new Tuple<string, string>(bar1, bar2);
        }
    }

    public class RequestContextProxyGrain : Grain, IRequestContextProxyGrain
    {
        public Task<Guid> E2EActivityId()
        {
            var grain = GrainFactory.GetGrain<IRequestContextTestGrain>((new Random()).Next());
            return grain.E2EActivityId();
        }
    }
}
