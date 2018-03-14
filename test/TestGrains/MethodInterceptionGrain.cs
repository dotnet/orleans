using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class OutgoingMethodInterceptionGrain : Grain, IOutgoingMethodInterceptionGrain
    {
        public async Task<Dictionary<string, object>> EchoViaOtherGrain(IMethodInterceptionGrain otherGrain, string message)
        {
            return new Dictionary<string, object>
            {
                ["result"] = await otherGrain.Echo(message)
            };
        }
    }

    public class MethodInterceptionGrain : Grain, IMethodInterceptionGrain, IIncomingGrainCallFilter
    {
        public Task<string> One()
        {
            throw new InvalidOperationException("Not allowed to actually invoke this method!");
        }

        [MessWithResult]
        public Task<string> Echo(string someArg) => Task.FromResult(someArg);

        public Task<string> NotIntercepted() => Task.FromResult("not intercepted");

        public Task<string> SayHello() => Task.FromResult("Hello");

        public Task<string> Throw()
        {
            throw new MyDomainSpecificException("Oi!");
        }

        public Task FilterThrows() => Task.CompletedTask;

        public Task<string> IncorrectResultType() => Task.FromResult("hop scotch");

        async Task IIncomingGrainCallFilter.Invoke(IIncomingGrainCallContext context)
        {
            var methodInfo = context.ImplementationMethod;
            if (methodInfo.Name == nameof(One) && methodInfo.GetParameters().Length == 0)
            {
                // Short-circuit the request and return to the caller without actually invoking the grain method.
                context.Result = "intercepted one with no args";
                return;
            }

            if (methodInfo.Name == nameof(IncorrectResultType))
            {
                // This method has a string return type, but we are setting the result to a Guid.
                // This should result in an invalid cast exception.
                context.Result = Guid.NewGuid();
                return;
            }

            if (methodInfo.Name == nameof(FilterThrows))
            {
                throw new MyDomainSpecificException("Filter THROW!");
            }

            // Invoke the request.
            try
            {
                await context.Invoke();
            }
            catch (MyDomainSpecificException e)
            {
                context.Result = "EXCEPTION! " + e.Message;
                return;
            }

            // To prove that the MethodInfo is from the implementation and not the interface,
            // we check for this attribute which is only present on the implementation. This could be
            // done in a simpler fashion, but this demonstrates a potential usage scenario.
            var shouldMessWithResult = methodInfo.GetCustomAttribute<MessWithResultAttribute>();
            var resultString = context.Result as string;
            if (shouldMessWithResult != null && resultString != null)
            {
                context.Result = string.Concat(resultString.Reverse());
            }
        }

        [Serializable]
        public class MyDomainSpecificException : Exception
        {
            public MyDomainSpecificException()
            {
            }

            public MyDomainSpecificException(string message) : base(message)
            {
            }

            protected MyDomainSpecificException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Method)]
        public class MessWithResultAttribute : Attribute
        {
        }
    }
    
    public class GenericMethodInterceptionGrain<T> : Grain, IGenericMethodInterceptionGrain<T>, IIncomingGrainCallFilter
    {
        public Task<string> SayHello() => Task.FromResult("Hello");

        public Task<string> GetInputAsString(T input) => Task.FromResult(input.ToString());
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.ImplementationMethod.Name == nameof(GetInputAsString))
            {
                context.Result = $"Hah! You wanted {context.Arguments[0]}, but you got me!";
                return;
            }

            await context.Invoke();
        }
    }
    
    public class TrickyInterceptionGrain : Grain, ITrickyMethodInterceptionGrain, IIncomingGrainCallFilter
    {
        public Task<string> SayHello() => Task.FromResult("Hello");
        
        public Task<string> GetInputAsString(string input) => Task.FromResult(input);

        public Task<string> GetInputAsString(bool input) => Task.FromResult(input.ToString(CultureInfo.InvariantCulture));

        public Task<int> GetBestNumber() => Task.FromResult(38);
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.ImplementationMethod.Name == nameof(GetInputAsString))
            {
                context.Result = $"Hah! You wanted {context.Arguments[0]}, but you got me!";
                return;
            }

            await context.Invoke();
        }
    }
    
    public class GrainCallFilterTestGrain : Grain, IGrainCallFilterTestGrain, IIncomingGrainCallFilter
    {
        private const string Key = GrainCallFilterTestConstants.Key;

        // Note, this class misuses the context. It should not be stored for later use.
        private IGrainCallContext context;

        public async Task<string> CallWithBadInterceptors(bool early, bool mid, bool late)
        {
            if (late)
            {
                this.context.Arguments[2] = false;
                await this.context.Invoke();
            }

            return $"I will {(early ? string.Empty : "not ")}misbehave!";
        }

        public Task<string> GetRequestContext() => Task.FromResult((string)RequestContext.Get(Key) + "4");

        public async Task Invoke(IIncomingGrainCallContext ctx)
        {
            //
            // NOTE: this grain demonstrates incorrect usage of grain call interceptors and should not be used
            // as an example of proper usage. Specifically, storing the context for later execution is invalid.
            //

            this.context = ctx;
            if (string.Equals(ctx.ImplementationMethod.Name, nameof(CallWithBadInterceptors)) && (bool)ctx.Arguments[0])
            {
                await ctx.Invoke();
            }

            if (RequestContext.Get(Key) is string value) RequestContext.Set(Key, value + '3');
            await ctx.Invoke();

            if (string.Equals(ctx.ImplementationMethod?.Name, nameof(CallWithBadInterceptors)) && (bool)ctx.Arguments[1])
            {
                await ctx.Invoke();
            }

            this.context = null;
        }
    }
}
