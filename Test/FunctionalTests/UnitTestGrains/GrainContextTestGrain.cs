using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTestGrains;
using UnitTests.GrainInterfaces;


namespace GrainContextTestGrain
{
    public class GrainContextTestGrain : Grain, IGrainContextTestGrain
    {

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<string> GetGrainContext_Immediate()
        {
            return Task.FromResult(RuntimeContext.CurrentActivationContext.ToString());
        }

        public Task<string> GetGrainContext_ContinueWithVoid()
        {
            TaskCompletionSource<string> resolver = new TaskCompletionSource<string>();

            ISimpleGrain dependency = SimpleGrainFactory.GetGrain((new Random()).Next(), UnitTestGrainInterfacesConstants.SimpleGrainNamePrefix);
            dependency.SetA(1).ContinueWith((_) =>
                {
                    resolver.SetResult(RuntimeContext.CurrentActivationContext.ToString());
                }).Ignore();

            return resolver.Task;
        }

        public Task<string> GetGrainContext_DoubleContinueWith()
        {
            TaskCompletionSource<string> resolver = new TaskCompletionSource<string>();

            ISimpleGrain dependency = SimpleGrainFactory.GetGrain((new Random()).Next(), UnitTestGrainInterfacesConstants.SimpleGrainNamePrefix);
            dependency.SetA(2).ContinueWith((_) =>
            {
                dependency.GetA().ContinueWith((Task<int> a) =>
                {
                    try
                    {
                        resolver.SetResult(a.Result.ToString() + RuntimeContext.CurrentActivationContext.ToString());
                    }
                    catch (Exception exc)
                    {
                        resolver.SetException(exc);
                    }
                }).Ignore();
            }).Ignore();

            return resolver.Task;
        }
        
        public Task<string> GetGrainContext_StartNew_Void()
        {
            TaskCompletionSource<string> resolver = new TaskCompletionSource<string>();

            Task.Factory.StartNew(()=>
            {
                resolver.SetResult(RuntimeContext.CurrentActivationContext.ToString());
            }).Ignore();

            return resolver.Task;
        }

        public Task<string> GetGrainContext_StartNew_Task()
        {
            return Task.Factory.StartNew(() =>
            {
                return RuntimeContext.CurrentActivationContext.ToString();
            });
        }

        public Task<string> GetGrainContext_ContinueWithException()
        {
            TaskCompletionSource<string> resolver = new TaskCompletionSource<string>();

            IErrorGrain errorGrain = ErrorGrainFactory.GetGrain((new Random()).Next());
            Task<int> promise = errorGrain.GetAxBError(1, 2);
  
            promise.ContinueWith((Task t) =>
            {
                if (!t.IsFaulted)
                {
                    resolver.SetException(
                        new ApplicationException(
                            "Exception block is expected to be called instead of the success continuation."));
                }
                else
                {
                    try
                    {
                        resolver.SetResult(RuntimeContext.CurrentActivationContext.ToString());
                    }
                    catch (Exception)
                    {
                        resolver.SetException(new ApplicationException("No grain context."));
                    }
                }
            }).Ignore();

            return resolver.Task;
        }

        public Task<string> GetGrainContext_ContinueWithValueException()
        {
            TaskCompletionSource<string> resolver = new TaskCompletionSource<string>();

            IErrorGrain errorGrain = ErrorGrainFactory.GetGrain((new Random()).Next());
            Task<int> promise = errorGrain.GetAxBError(1, 2);
            bool doThrow = true;
            promise.ContinueWith<string>((Task t) =>
            {
                if (t.IsFaulted)
                {
                    try
                    {
                        resolver.SetResult(RuntimeContext.CurrentActivationContext.ToString());
                    }
                    catch (Exception)
                    {
                        resolver.SetException(new ApplicationException("No grain context."));
                    }
                    return "";
                }
                if (doThrow)
                {
                    resolver.SetException(new ApplicationException("Exception block is expected to be called instead of the success continuation."));
                }
                return "";
            }).Ignore();

            return resolver.Task;
        }

        public Task<string> GetGrainContext_ContinueWithValueAction()
        {
            TaskCompletionSource<string> resolver = new TaskCompletionSource<string>();

            IErrorGrain errorGrain = ErrorGrainFactory.GetGrain((new Random()).Next());
            Task<int> promise = errorGrain.GetAxB(1, 2);

            promise.ContinueWith((Task t) =>
            {
                if (!t.IsFaulted)
                {
                    try
                    {
                        resolver.SetResult(RuntimeContext.CurrentActivationContext.ToString());
                    }
                    catch (Exception)
                    {
                        resolver.SetException(new ApplicationException("No grain context."));
                    }
                }
                else
                {
                    resolver.SetException(new ApplicationException("Continuation is expected to be called instead of the exception block."));
                }
            }).Ignore();

            return resolver.Task;
        }

        public Task<string> GetGrainContext_ContinueWithValueActionException()
        {
            TaskCompletionSource<string> resolver = new TaskCompletionSource<string>();

            IErrorGrain errorGrain = ErrorGrainFactory.GetGrain((new Random()).Next());
            Task<int> promise = errorGrain.GetAxBError(1, 2);

            promise.ContinueWith((Task t) =>
            {
                if (!t.IsFaulted)
                {
                    resolver.SetException(new ApplicationException("Exception block is expected to be called instead of the success continuation."));
                }
                else
                {
                    try
                    {
                        resolver.SetResult(RuntimeContext.CurrentActivationContext.ToString());
                    }
                    catch (Exception)
                    {
                        resolver.SetException(new ApplicationException("No grain context."));
                    }
                }
            }).Ignore();

            return resolver.Task;
        }

        public async Task<string> GetGrainContext_ContinueWithValueFunction()
        {
            IErrorGrain errorGrain = ErrorGrainFactory.GetGrain((new Random()).Next());
            Task<int> promise = errorGrain.GetAxB(1, 2);
            await promise;
            return RuntimeContext.CurrentActivationContext.ToString();
        }
    }
}
