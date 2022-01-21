using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
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
            logger.Info("Activate.");
            return Task.CompletedTask;
        }

        public Task LogMessage(string msg)
        {
           logger.Info(msg);
           return Task.CompletedTask;
        }

        public Task SetAError(int a)
        {
            logger.Info("SetAError={0}", a);
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
            logger.Info("DelayMethod {0}.", counter);
            counter++;
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds));
        }

        public Task Dispose()
        {
            logger.Info("Dispose()");
            return Task.CompletedTask;
        }

        public Task<int> UnobservedErrorImmediate()
        {
            logger.Info("UnobservedErrorImmediate()");

            bool doThrow = true;
            // the grain method returns OK, but leaves some unobserved promise
            Task<long> promise = Task<long>.Factory.StartNew(() =>
            {
                if (!doThrow)
                    return 0;
                logger.Info("About to throw 1.");
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
            logger.Info("UnobservedErrorDelayed()");
            bool doThrow = true;
            // the grain method rturns OK, but leaves some unobserved promise
            Task<long> promise = Task<long>.Factory.StartNew(() =>
            {
                if (!doThrow)
                    return 0;
                Thread.Sleep(100);
                logger.Info("About to throw 1.5.");
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
            logger.Info("UnobservedErrorContinuation2()");
            // the grain method returns OK, but leaves some unobserved promise
            Task<long> promise = Task.FromResult((long)25);
            Task cont = promise.ContinueWith(_ =>
                {
                    logger.Info("About to throw 2.");
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
            logger.Info("UnobservedErrorContinuation3() from Task " + Task.CurrentId);
            // the grain method returns OK, but leaves some unobserved promise
            Task<long> promise = Task<long>.Factory.StartNew(() =>
            {
                logger.Info("First promise from Task " + Task.CurrentId);
                return 26;
            });
            Task cont = promise.ContinueWith(_ =>
            {
                logger.Info("About to throw 3 from Task " + Task.CurrentId);
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
            logger.Info("UnobservedIgnoredError()");
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
