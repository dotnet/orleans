using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class ErrorGrain : SimpleGrain, IErrorGrain
    {
        private int counter;

        public ErrorGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Activate.");
            return Task.CompletedTask;
        }

        public Task LogMessage(string msg)
        {
            logger.LogInformation("{Message}", msg);
           return Task.CompletedTask;
        }

        public Task SetAError(int a)
        {
            logger.LogInformation("SetAError={A}", a);
            A = a;
            throw new Exception("SetAError-Exception");
        }

        public Task SetBError(int a)
        {
            throw new Exception("SetBError-Exception");
        }

        public Task<int> GetAxBError()
        {
            throw new Exception("GetAxBError-Exception");
        }

        public Task<int> GetAxBError(int a, int b)
        {
            throw new Exception("GetAxBError(a,b)-Exception");
        }

        public Task LongMethod(int waitTime)
        {
            Thread.Sleep(waitTime);
            return Task.CompletedTask;
        }

        public Task LongMethodWithError(int waitTime)
        {
            Thread.Sleep(waitTime);
            throw new Exception("LongMethodWithError");
        }

        public async Task DelayMethod(int milliseconds)
        {
            logger.LogInformation("DelayMethod {Counter}.", counter);
            counter++;
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds));
        }

        public Task Dispose()
        {
            logger.LogInformation("Dispose()");
            return Task.CompletedTask;
        }

        public Task<int> UnobservedErrorImmediate()
        {
            logger.LogInformation("UnobservedErrorImmediate()");

            bool doThrow = true;
            // the grain method returns OK, but leaves some unobserved promise
            Task<long> promise = Task<long>.Factory.StartNew(() =>
            {
                if (!doThrow)
                    return 0;
                logger.LogInformation("About to throw 1.");
                throw new ArgumentException("ErrorGrain left Immediate Unobserved Error 1.");
            });
            promise = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return Task.FromResult(11);
        }

        public Task<int> UnobservedErrorDelayed()
        {
            logger.LogInformation("UnobservedErrorDelayed()");
            bool doThrow = true;
            // the grain method rturns OK, but leaves some unobserved promise
            Task<long> promise = Task<long>.Factory.StartNew(() =>
            {
                if (!doThrow)
                    return 0;
                Thread.Sleep(100);
                logger.LogInformation("About to throw 1.5.");
                throw new ArgumentException("ErrorGrain left Delayed Unobserved Error 1.5.");
            });
            promise = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return Task.FromResult(11);
        }

        public Task<int> UnobservedErrorContinuation2()
        {
            logger.LogInformation("UnobservedErrorContinuation2()");
            // the grain method returns OK, but leaves some unobserved promise
            Task<long> promise = Task.FromResult((long)25);
            Task cont = promise.ContinueWith(_ =>
                {
                    logger.LogInformation("About to throw 2.");
                    throw new ArgumentException("ErrorGrain left ContinueWith Unobserved Error 2.");
                });
            promise = null;
            cont = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return Task.FromResult(11);
        }

        public Task<int> UnobservedErrorContinuation3()
        {
            logger.LogInformation("UnobservedErrorContinuation3() from Task {TaskId}", Task.CurrentId);
            // the grain method returns OK, but leaves some unobserved promise
            Task<long> promise = Task<long>.Factory.StartNew(() =>
            {
                logger.LogInformation("First promise from Task {TaskId}", Task.CurrentId);
                return 26;
            });
            Task cont = promise.ContinueWith(_ =>
            {
                logger.LogInformation("About to throw 3 from Task {TaskId}", Task.CurrentId);
                throw new ArgumentException("ErrorGrain left ContinueWith Unobserved Error 3.");
            });
            //logger.Info("cont.number=" + cont.task.number + " cont.m_Task.number=" + cont.task.m_Task.Id);
            promise = null;
            cont = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return Task.FromResult(11);
        }

        public Task<int> UnobservedIgnoredError()
        {
            logger.LogInformation("UnobservedIgnoredError()");
            bool doThrow = true;
            // the grain method rturns OK, but leaves some unobserved promise
            Task<long> promise = Task<long>.Factory.StartNew(() =>
            {
                if (!doThrow)
                    return 0;
                throw new ArgumentException("ErrorGrain left Unobserved Error, but asked to ignore it later.");
            });
            promise.Ignore();
            return Task.FromResult(11);
        }

        public Task AddChildren(List<IErrorGrain> children)
        {
            return Task.CompletedTask;
        }

        public async Task<bool> ExecuteDelayed(TimeSpan delay)
        {
            object ctxBefore = RuntimeContext.Current;

            await Task.Delay(delay);
            object ctxInside = RuntimeContext.Current;
            return ReferenceEquals(ctxBefore, ctxInside);
        }
    }
}
