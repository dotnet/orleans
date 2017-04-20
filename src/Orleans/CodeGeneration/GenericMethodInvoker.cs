using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
        private static readonly ConcurrentDictionary<Type, MethodInfo> BoxMethods = new ConcurrentDictionary<Type, MethodInfo>();
        private static readonly Func<Type, MethodInfo> CreateBoxMethod = GetTaskConversionMethod;
        private static readonly MethodInfo GenericMethodInvokerDelegateMethodInfo =
            TypeUtils.Method((GenericMethodInvokerDelegate del) => del.Invoke(null, null));
        private static readonly ILFieldBuilder FieldBuilder = new ILFieldBuilder();

        private readonly MethodInfo genericMethodInfo;
        private readonly Type grainInterfaceType;
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
            this.typeParameterCount = typeParameterCount;
            this.invokers = new ConcurrentDictionary<object[], GenericMethodInvokerDelegate>(this);
            this.genericMethodInfo = GetMethod(grainInterfaceType, methodName, typeParameterCount);
            this.createInvoker = this.CreateInvoker;
        }
        
        /// <summary>
        /// Invoke the defined method on the provided <paramref name="grain"/> instance with the given <paramref name="arguments"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="arguments">The arguments to the method with the type parameters first, followed by the method parameters.</param>
        /// <returns>The invocation result.</returns>
        public Task<object> Invoke(IAddressable grain, object[] arguments)
        {
            var invoker = this.invokers.GetOrAdd(arguments, this.createInvoker);
            return invoker(grain, arguments);
        }

        /// <summary>
        /// Creates an invoker delegate for the type arguments specified in <paramref name="arguments"/>.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        /// <returns>A new invoker delegate.</returns>
        private GenericMethodInvokerDelegate CreateInvoker(object[] arguments)
        {
            // First, create the concrete method which will be called.
            var typeParameters = arguments.Take(this.typeParameterCount).Cast<Type>().ToArray();
            var concreteMethod = this.genericMethodInfo.MakeGenericMethod(typeParameters);

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

                // Skip the type parameters and load the particular argument.
                il.LoadConstant(i + this.typeParameterCount); 
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
            if (taskType == typeof(Task)) return TypeUtils.Method((Task task) => task.Box());
            if (taskType == typeof(void)) return TypeUtils.Property(() => TaskDone.Done).GetMethod;

            if (taskType.GetGenericTypeDefinition() != typeof(Task<>))
                throw new ArgumentException($"Unsupported return type {taskType}.");
            var innerType = taskType.GenericTypeArguments[0];
            var methods = typeof(PublicOrleansTaskExtensions).GetMethods(BindingFlags.Static | BindingFlags.Public);
            foreach (var method in methods)
            {
                if (method.Name != nameof(PublicOrleansTaskExtensions.Box) || !method.ContainsGenericParameters) continue;
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

            // Since this equality compararer only compares type parameters, ignore any elements after
            // the defined type parameter count.
            if (x.Length < this.typeParameterCount || y.Length < this.typeParameterCount) return false;

            // Compare each type parameter.
            for (var i = 0; i < this.typeParameterCount; i++)
            {
                if (x[i] as Type != y[i] as Type) return false;
            }

            return true;
        }

        /// <summary>
        /// Returns a hash code for the type parameters in the provided argument list.
        /// </summary>
        /// <param name="obj">The argument list.</param>
        /// <returns>A hash code.</returns>
        int IEqualityComparer<object[]>.GetHashCode(object[] obj)
        {
            if (obj == null || obj.Length == 0) return 0;
            unchecked
            {
                // Only consider the type parameters.
                var result = 0;
                for (var i = 0; i < this.typeParameterCount && i < obj.Length; i++)
                {
                    var type = obj[i] as Type;
                    if (type == null) break;

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
        /// <param name="typeParameterCount">The number of generic type parameters.</param>
        /// <returns>The identified method.</returns>
        private static MethodInfo GetMethod(Type declaringType, string methodName, int typeParameterCount)
        {
            var methods = declaringType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            foreach (var method in methods)
            {
                if (!method.IsGenericMethodDefinition) continue;
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
                if (method.GetGenericArguments().Length != typeParameterCount) continue;

                return method;
            }

            throw new ArgumentException(
                $"Could not find generic method {declaringType}.{methodName}<{new string(',', typeParameterCount)}>(...).");
        }
    }
}
