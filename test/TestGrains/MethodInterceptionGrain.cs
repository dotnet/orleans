﻿using System.Globalization;
using System.Threading.Tasks;

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
}
