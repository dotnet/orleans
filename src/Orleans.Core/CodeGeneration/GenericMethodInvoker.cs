using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Functionality for invoking calls on a generic instance method.
    /// </summary>
    /// <remarks>
    /// Each instance of this class can invoke calls on one generic method.
    /// </remarks>
    public class GenericMethodInvoker : IEqualityComparer<object[]>
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> BoxMethods =
            new ConcurrentDictionary<Type, MethodInfo>();

        private static readonly Func<Type, MethodInfo> CreateBoxMethod = GetTaskConversionMethod;

        private static readonly MethodInfo GenericMethodInvokerDelegateMethodInfo =
            TypeUtils.Method((GenericMethodInvokerDelegate del) => del.Invoke(null, null));

        private static readonly ILFieldBuilder FieldBuilder = new ILFieldBuilder();

        private readonly Type grainInterfaceType;
        private readonly string methodName;
        private readonly int typeParameterCount;
        private readonly ConcurrentDictionary<object[], GenericMethodInvokerDelegate> invokers;
        private readonly Func<object[], GenericMethodInvokerDelegate> createInvoker;

        /// <summary>
        ///  Invoke the generic method described by this instance on the provided <paramref name="grain"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="arguments">The arguments, including the method's type parameters.</param>
        /// <returns>The method result.</returns>
        private delegate Task<object> GenericMethodInvokerDelegate(IAddressable grain, object[] arguments);

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMethodInvoker"/> class.
        /// </summary>
        /// <param name="grainInterfaceType">The grain interface type which the method exists on.</param>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="typeParameterCount">The number of type parameters which the method has.</param>
        public GenericMethodInvoker(Type grainInterfaceType, string methodName, int typeParameterCount)
        {
            this.grainInterfaceType = grainInterfaceType;
            this.methodName = methodName;
            this.typeParameterCount = typeParameterCount;
            this.invokers = new ConcurrentDictionary<object[], GenericMethodInvokerDelegate>(this);
            this.createInvoker = this.CreateInvoker;
        }

        /// <summary>
        /// Invoke the defined method on the provided <paramref name="grain"/> instance with the given <paramref name="arguments"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="arguments">The arguments to the method with the type parameters first, followed by the method parameters types, and finally the parameter values..</param>
        /// <returns>The invocation result.</returns>
        public Task<object> Invoke(IAddressable grain, object[] arguments)
        {
            var argc = (arguments.Length - typeParameterCount) / 2;

            // As this is on a hot path, avoid allocating (LINQ) as much as possible
            var argv = arguments.AsSpan();

            // generic parameter type(s) + argument type(s) -- this is our invokers' cache-key
            var types = argv.Slice(0, typeParameterCount + argc);

            // argument values to be passed
            var argValues = argv.Slice(typeParameterCount + argc, argc);

            var invoker = this.invokers.GetOrAdd(types.ToArray(), this.createInvoker);

            return invoker(grain, argValues.ToArray());
        }

        /// <summary>
        /// Creates an invoker delegate for the type arguments specified in <paramref name="arguments"/>.
        /// </summary>
        /// <param name="arguments">The method arguments, including one or more type parameter(s) at the head of the array..</param>
        /// <returns>A new invoker delegate.</returns>
        private GenericMethodInvokerDelegate CreateInvoker(object[] arguments)
        {
            // obtain the generic type parameter(s)
            var typeParameters = arguments.Take(this.typeParameterCount).Cast<Type>().ToArray();

            // obtain the method argument type(s)
            var parameterTypes = arguments
                .Skip(typeParameterCount)
                .Cast<Type>()
                .ToArray();

            // get open generic method for this arity/parameter combination
            var openGenericMethodInfo = GetMethod(
                grainInterfaceType,
                methodName,
                typeParameters,
                parameterTypes);

            // close the generic type
            var concreteMethod = openGenericMethodInfo.MakeGenericMethod(typeParameters);

            // Next, create a delegate which will call the method on the grain, pushing each of the arguments,
            var il = new ILDelegateBuilder<GenericMethodInvokerDelegate>(
                FieldBuilder,
                $"GenericMethodInvoker_{this.grainInterfaceType}_{concreteMethod.Name}",
                GenericMethodInvokerDelegateMethodInfo);

            // Load the grain and cast it to the type the concrete method is declared on.
            // Eg: cast from IAddressable to IGrainWithGenericMethod.
            il.LoadArgument(0);
            il.CastOrUnbox(this.grainInterfaceType);

            // Load each of the method parameters from the argument array, skipping the type parameters.
            var methodParameters = concreteMethod.GetParameters();
            for (var i = 0; i < methodParameters.Length; i++)
            {
                il.LoadArgument(1); // Load the argument array.

                // load the particular argument.
                il.LoadConstant(i);

                il.LoadReferenceElement();

                // Cast the argument from 'object' to the type expected by the concrete method.
                il.CastOrUnbox(methodParameters[i].ParameterType);
            }

            // Call the concrete method.
            il.Call(concreteMethod);

            // If the result type is Task or Task<T>, convert it to Task<object>.
            var returnType = concreteMethod.ReturnType;
            if (returnType != typeof(Task<object>))
            {
                var boxMethod = BoxMethods.GetOrAdd(returnType, CreateBoxMethod);
                il.Call(boxMethod);
            }

            // Return the resulting Task<object>.
            il.Return();
            return il.CreateDelegate();
        }

        /// <summary>
        /// Returns a suitable <see cref="MethodInfo"/> for a method which will convert an argument of type <paramref name="taskType"/>
        /// into <see cref="Task{Object}"/>.
        /// </summary>
        /// <param name="taskType">The type to convert.</param>
        /// <returns>A suitable conversion method.</returns>
        private static MethodInfo GetTaskConversionMethod(Type taskType)
        {
            if (taskType == typeof(Task)) return TypeUtils.Method((Task task) => task.ToUntypedTask());
            if (taskType == typeof(Task<object>)) return TypeUtils.Method((Task<object> task) => task.ToUntypedTask());
            if (taskType == typeof(void)) return TypeUtils.Property(() => Task.CompletedTask).GetMethod;

            if (taskType.GetGenericTypeDefinition() != typeof(Task<>))
                throw new ArgumentException($"Unsupported return type {taskType}.");
            var innerType = taskType.GenericTypeArguments[0];
            var methods = typeof(OrleansTaskExtentions).GetMethods(BindingFlags.Static | BindingFlags.Public);
            foreach (var method in methods)
            {
                if (method.Name != nameof(OrleansTaskExtentions.ToUntypedTask) ||
                    !method.ContainsGenericParameters) continue;
                return method.MakeGenericMethod(innerType);
            }

            throw new ArgumentException($"Could not find conversion method for type {taskType}");
        }

        /// <summary>
        /// Performs equality comparison for the purpose of comparing type parameters only.
        /// </summary>
        /// <param name="x">One argument list.</param>
        /// <param name="y">The other argument list.</param>
        /// <returns><see langword="true"/> if the type parameters in the respective arguments are equal, <see langword="false"/> otherwise.</returns>
        bool IEqualityComparer<object[]>.Equals(object[] x, object[] y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(null, y)) return false;

            return x.SequenceEqual(y);
        }

        /// <summary>
        /// Returns a hash code for the provided argument list.
        /// </summary>
        /// <param name="obj">The argument list.</param>
        /// <returns>A hash code.</returns>
        int IEqualityComparer<object[]>.GetHashCode(object[] obj)
        {
            if (obj.Length == 0) return 0;

            unchecked
            {
                var result = 0;
                foreach (var type in obj)
                {
                    result = (result * 367) ^ type.GetHashCode();
                }

                return result;
            }
        }

        /// <summary>
        /// Returns the <see cref="MethodInfo"/> for the method on <paramref name="declaringType"/> with the provided name
        /// and number of generic type parameters.
        /// </summary>
        /// <param name="declaringType">The type which the method is declared on.</param>
        /// <param name="methodName">The method name.</param>
        /// <param name="typeParameters">The generic type parameters to use.</param>
        /// <param name="parameterTypes"></param>
        /// <returns>The identified method.</returns>
        private static MethodInfo GetMethod(
            Type declaringType,
            string methodName,
            Type[] typeParameters,
            Type[] parameterTypes
        )
        {
            MethodInfo methodInfo = null;

            var typeParameterCount = typeParameters.Length;

            bool skipMethod = false;

            var methods = declaringType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            foreach (var openMethod in methods)
            {
                if (!openMethod.IsGenericMethodDefinition) continue;

                // same name?
                if (!string.Equals(openMethod.Name, methodName, StringComparison.Ordinal)) continue;

                // same type parameter count?
                if (openMethod.GetGenericArguments().Length != typeParameterCount) continue;

                // close the definition
                MethodInfo closedMethod = openMethod.MakeGenericMethod(typeParameters);
                
                // obtain list of closed parameters (no generic placeholders any more)
                var parameterInfos = closedMethod.GetParameters();

                // same number of params?
                if (parameterInfos.Length == parameterTypes.Length)
                {
                    for (int i = 0; i < parameterInfos.Length; ++i)
                    {
                        // validate compatibility - assignable/covariant array etc.
                        if (!parameterInfos[i].ParameterType.IsAssignableFrom(parameterTypes[i]))
                        {
                            skipMethod = true;
                            break;
                        }
                    }

                    if (skipMethod)
                    {
                        skipMethod = false;
                        continue;
                    }

                    // found compatible overload; return generic definition, not closed method
                    methodInfo = openMethod;
                    break;
                }
            } // next method


            if (methodInfo is null)
            {
                var signature = string.Join(",",
                    parameterTypes.Select(t => t.Name));

                var typeParams = string.Join(",", typeParameters.Select(t => t.Name));

                throw new ArgumentException(
                    $"Could not find exact match for generic method {declaringType}.{methodName}" +
                    $"<{typeParams}>({signature}).");
            }

            return methodInfo;
        }
    }
}
