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
    public class OutgoingMethodInterceptionGrain : IOutgoingMethodInterceptionGrain
    {
        public async Task<Dictionary<string, object>> EchoViaOtherGrain(IMethodInterceptionGrain otherGrain, string message)
        {
            return new Dictionary<string, object>
            {
                ["result"] = await otherGrain.Echo(message)
            };
        }

        public Task<string> ThrowIfGreaterThanZero(int value)
        {
            if (value > 0)
            {
                throw new ArgumentOutOfRangeException($"{value} is greater than zero!");
            }

            return Task.FromResult("Thanks for nothing");
        }
    }

    public class MethodInterceptionGrain : IMethodInterceptionGrain, IIncomingGrainCallFilter
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

        public Task SystemWideCallFilterMarker() => Task.CompletedTask;

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
        [GenerateSerializer]
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

    public class GenericMethodInterceptionGrain<T> : IGenericMethodInterceptionGrain<T>, IIncomingGrainCallFilter
    {
        public Task<string> SayHello() => Task.FromResult("Hello");

        public Task<string> GetInputAsString(T input) => Task.FromResult(input.ToString());
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.ImplementationMethod.Name == nameof(GetInputAsString))
            {
                context.Result = $"Hah! You wanted {context.Request.GetArgument(0)}, but you got me!";
                return;
            }

            await context.Invoke();
        }
    }

    public class TrickyInterceptionGrain : ITrickyMethodInterceptionGrain, IIncomingGrainCallFilter
    {
        public Task<string> SayHello() => Task.FromResult("Hello");

        public Task<string> GetInputAsString(string input) => Task.FromResult(input);

        public Task<string> GetInputAsString(bool input) => Task.FromResult(input.ToString(CultureInfo.InvariantCulture));

        public Task<int> GetBestNumber() => Task.FromResult(38);
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.ImplementationMethod.Name == nameof(GetInputAsString))
            {
                context.Result = $"Hah! You wanted {context.Request.GetArgument(0)}, but you got me!";
                return;
            }

            await context.Invoke();
        }
    }

    public class GrainCallFilterTestGrain : IGrainCallFilterTestGrain, IIncomingGrainCallFilter
    {
        private const string Key = GrainCallFilterTestConstants.Key;

        public Task<string> ThrowIfGreaterThanZero(int value)
        {
            if (value > 0)
            {
                throw new ArgumentOutOfRangeException($"{value} is greater than zero!");
            }

            return Task.FromResult("Thanks for nothing");
        }

        public Task<string> GetRequestContext() => Task.FromResult((string)RequestContext.Get(Key) + "4");

        public async Task Invoke(IIncomingGrainCallContext ctx)
        {
            var attemptsRemaining = 2;

            while (attemptsRemaining > 0)
            {
                try
                {
                    var interfaceMethod = ctx.InterfaceMethod ?? throw new ArgumentException("InterfaceMethod is null!");
                    var implementationMethod = ctx.ImplementationMethod ?? throw new ArgumentException("ImplementationMethod is null!");
                    if (!string.Equals(implementationMethod.Name, interfaceMethod.Name))
                    {
                        throw new ArgumentException("InterfaceMethod.Name != ImplementationMethod.Name");
                    }

                    if (string.Equals(implementationMethod.Name, nameof(GrainSpecificCallFilterMarker)))
                    {
                        // explicitely do not continue calling Invoke
                        return;
                    }

                    if (RequestContext.Get(Key) is string value) RequestContext.Set(Key, value + '3');
                    await ctx.Invoke();
                    return;
                }
                catch (ArgumentOutOfRangeException) when (attemptsRemaining > 1)
                {
                    if (string.Equals(ctx.ImplementationMethod?.Name, nameof(ThrowIfGreaterThanZero)) && ctx.Request.GetArgument(0) is int value)
                    {
                        ctx.Request.SetArgument(0, value - 1);
                    }

                    --attemptsRemaining;
                }
            }
        }

        public Task<int> SumSet(HashSet<int> numbers)
        {
            return Task.FromResult(numbers.Sum());
        }

        public Task SystemWideCallFilterMarker()
        {
            return Task.CompletedTask;
        }

        public Task GrainSpecificCallFilterMarker()
        {
            return Task.CompletedTask;
        }
    }

    public class CaterpillarGrain : ICaterpillarGrain, IIncomingGrainCallFilter
    {
        Task IIncomingGrainCallFilter.Invoke(IIncomingGrainCallContext ctx)
        {
            if (ctx.InterfaceMethod is null) throw new Exception("InterfaceMethod is null");
            if (!ctx.InterfaceMethod.DeclaringType.IsInterface) throw new Exception("InterfaceMethod is not an interface method");

            if (ctx.ImplementationMethod is null) throw new Exception("ImplementationMethod is null");
            if (ctx.ImplementationMethod.DeclaringType.IsInterface) throw new Exception("ImplementationMethod is an interface method");

            if (RequestContext.Get("tag") is string tag)
            {
                var ifaceTag = ctx.InterfaceMethod.GetCustomAttribute<TestMethodTagAttribute>()?.Tag;
                var implTag = ctx.ImplementationMethod.GetCustomAttribute<TestMethodTagAttribute>()?.Tag;
                if (!string.Equals(tag, ifaceTag, StringComparison.Ordinal)
                    || !string.Equals(tag, implTag, StringComparison.Ordinal))
                {
                    throw new Exception($"Expected method tags to be equal to request context tag: RequestContext: {tag} Interface: {ifaceTag} Implementation: {implTag}");
                }
            }

            return ctx.Invoke();
        }

        [TestMethodTag("hungry-eat")]
        public Task Eat(Apple food) => Task.CompletedTask;

        [TestMethodTag("omnivore-eat")]
        Task IOmnivoreGrain.Eat<T>(T food) => Task.CompletedTask;

        [TestMethodTag("caterpillar-eat")]
        public Task Eat<T>(T food) => Task.CompletedTask;

        [TestMethodTag("hungry-eatwith")]
        public Task EatWith<U>(Apple food, U condiment) => Task.CompletedTask;
    }
}
