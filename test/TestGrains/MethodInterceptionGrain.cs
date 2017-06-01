﻿using System.Globalization;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;

namespace UnitTests.Grains
{
    using System;
    using System.Linq;
    using System.Reflection;
    using Orleans;
    using Orleans.CodeGeneration;
    using UnitTests.GrainInterfaces;
    public class MethodInterceptionGrain : Grain, IMethodInterceptionGrain, IGrainInvokeInterceptor
    {
        public async Task<object> Invoke(MethodInfo methodInfo, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            if (methodInfo.Name == nameof(One) && methodInfo.GetParameters().Length == 0)
            {
                if (request.MethodId != 14142) throw new Exception($"Method id of 'One' must be 14142, not {request.MethodId}.");
                return "intercepted one with no args";
            }

            var result = await invoker.Invoke(this, request);

            // To prove that the MethodInfo is from the implementation and not the interface,
            // we check for this attribute which is only present on the implementation. This could be
            // done in a simpler fashion, but this demonstrates a potential usage scenario.
            var shouldMessWithResult = methodInfo.GetCustomAttribute<MessWithResultAttribute>();
            var resultString = result as string;
            if (shouldMessWithResult != null && resultString != null)
            {
                result = string.Concat(resultString.Reverse());
            }

            return result;
        }

        public Task<string> One()
        {
            throw new InvalidOperationException("Not allowed to actually invoke this method!");
        }

        [MessWithResult]
        public Task<string> Echo(string someArg)
        {
            return Task.FromResult(someArg);
        }

        public Task<string> NotIntercepted()
        {
            return Task.FromResult("not intercepted");
        }

        [AttributeUsage(AttributeTargets.Method)]
        public class MessWithResultAttribute : Attribute
        {
        }

        public Task<string> SayHello()
        {
            return Task.FromResult("Hello");
        }
    }

    public class GenericMethodInterceptionGrain<T> : Grain, IGenericMethodInterceptionGrain<T>, IGrainInvokeInterceptor
    {
        public Task<object> Invoke(MethodInfo methodInfo, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            if (methodInfo.Name == nameof(GetInputAsString))
            {
                return Task.FromResult<object>($"Hah! You wanted {request.Arguments[0]}, but you got me!");
            }

            return invoker.Invoke(this, request);
        }

        public Task<string> SayHello() => Task.FromResult("Hello");

        public Task<string> GetInputAsString(T input) => Task.FromResult(input.ToString());
    }

    public class TrickyInterceptionGrain : Grain, ITrickyMethodInterceptionGrain, IGrainInvokeInterceptor
    {
        public Task<object> Invoke(MethodInfo methodInfo, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            if (methodInfo.Name == nameof(GetInputAsString))
            {
                return Task.FromResult<object>($"Hah! You wanted {request.Arguments[0]}, but you got me!");
            }

            return invoker.Invoke(this, request);
        }

        public Task<string> SayHello() => Task.FromResult("Hello");
        
        public Task<string> GetInputAsString(string input) => Task.FromResult(input);

        public Task<string> GetInputAsString(bool input) => Task.FromResult(input.ToString(CultureInfo.InvariantCulture));

        public Task<int> GetBestNumber() => Task.FromResult(38);
    }

    public class GrainCallFilterTestGrain : Grain, IGrainCallFilterTestGrain, IGrainCallFilter, IGrainInvokeInterceptor
    {
        private const string Key = GrainCallFilterTestConstants.Key;
        private IGrainCallContext context;

        public async Task<string> Execute(bool early, bool mid, bool late)
        {
            if (late)
            {
                this.context.Arguments[2] = false;
                await this.context.Invoke();
            }

            return $"I will {(early ? string.Empty : "not ")}misbehave!";
        }

        public Task<string> GetRequestContext() => Task.FromResult((string)RequestContext.Get(Key) + "!");

        public async Task Invoke(IGrainCallContext ctx)
        {
            this.context = ctx;
            var value = RequestContext.Get(Key) as string;
            if (value != null) RequestContext.Set(Key, value + 'i');
            await ctx.Invoke();
            this.context = null;
        }

        public async Task<object> Invoke(MethodInfo method, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            var value = RequestContext.Get(Key) as string;
            if (value != null) RequestContext.Set(Key, value + 'n');

            if (string.Equals(method?.Name, nameof(Execute)) && (bool)request.Arguments[0])
            {
                await context.Invoke();
            }

            var result = await invoker.Invoke(this, request);

            if (string.Equals(method?.Name, nameof(Execute)) && (bool)request.Arguments[1])
            {
                await context.Invoke();
            }

            return result;
        }
    }
}
