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
    public class GenericMethodInvoker : IEqualityComparer<object[]>
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> BoxMethods = new ConcurrentDictionary<Type, MethodInfo>();
        private static readonly Func<Type, MethodInfo> CreateBoxMethod = GetBoxMethod;
        private static readonly MethodInfo GenericMethodInvokerDelegateMethodInfo =
            TypeUtils.Method((GenericMethodInvokerDelegate del) => del.Invoke(null, null));
        private static readonly ILFieldBuilder FieldBuilder = new ILFieldBuilder();

        private readonly MethodInfo genericMethodInfo;
        private readonly Type grainInterfaceType;
        private readonly int typeParameterCount;

        private readonly ConcurrentDictionary<object[], GenericMethodInvokerDelegate> invokers;
        private readonly Func<object[], GenericMethodInvokerDelegate> createInvoker;

        delegate Task<object> GenericMethodInvokerDelegate(IAddressable grain, object[] arguments);

        public GenericMethodInvoker(Type grainInterfaceType, string methodName, int typeParameterCount)
        {
            this.grainInterfaceType = grainInterfaceType;
            this.typeParameterCount = typeParameterCount;
            this.invokers = new ConcurrentDictionary<object[], GenericMethodInvokerDelegate>(this);
            this.genericMethodInfo = GetMethod(grainInterfaceType, methodName, typeParameterCount);
            this.createInvoker = this.CreateInvoker;
        }
        
        public Task<object> Invoke(IAddressable grain, object[] arguments)
        {
            var invoker = this.invokers.GetOrAdd(arguments, this.createInvoker);
            return invoker(grain, arguments);
        }

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

            // Load the grain and cast it into the appropriate type.
            il.LoadArgument(0);
            il.CastOrUnbox(this.grainInterfaceType);

            // Load every argument from the argument array.
            var methodParameters = concreteMethod.GetParameters();
            for (var i = 0; i < methodParameters.Length; i++)
            {
                il.LoadArgument(1); // Load the argument array.

                // The argument is offset by the type parameter count, since type parameters come first.
                il.LoadConstant(i + this.typeParameterCount); 
                il.LoadReferenceElement();
                il.CastOrUnbox(methodParameters[i].ParameterType);
            }

            // Call the target method.
            il.Call(concreteMethod);

            // If the result type is Task or Task<T>, convert it to Task<object>.
            var returnType = concreteMethod.ReturnType;
            if (returnType != typeof(Task<object>))
            {
                var boxMethod = BoxMethods.GetOrAdd(returnType, CreateBoxMethod);
                il.Call(boxMethod);
            }

            il.Return();
            return il.CreateDelegate();
        }

        private static MethodInfo GetBoxMethod(Type returnType)
        {
            if (returnType == typeof(Task)) return TypeUtils.Method((Task task) => task.Box());
            if (returnType == typeof(void)) return TypeUtils.Property(() => TaskDone.Done).GetMethod;

            if (returnType.GetGenericTypeDefinition() != typeof(Task<>))
                throw new ArgumentException($"Unsupported return type {returnType}.");
            var innerType = returnType.GenericTypeArguments[0];
            var methods = typeof(PublicOrleansTaskExtensions).GetMethods(BindingFlags.Static | BindingFlags.Public);
            foreach (var method in methods)
            {
                if (method.Name != "Box" || !method.ContainsGenericParameters) continue;
                return method.MakeGenericMethod(innerType);
            }

            throw new ArgumentException($"Could not find Box method for type {returnType}");
        }

        bool IEqualityComparer<object[]>.Equals(object[] x, object[] y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(null, y)) return false;

            if (x.Length < this.typeParameterCount || y.Length < this.typeParameterCount) return false;

            for (var i = 0; i < this.typeParameterCount; i++)
            {
                if (!CompareAsTypes(x[i] as Type, y[i] as Type)) return false;
            }

            return true;
        }

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

        private static bool CompareAsTypes(Type x, Type y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(null, y)) return false;
            return x == y;
        }

        private static MethodInfo GetMethod(Type grainInterfaceType, string methodName, int typeParameterCount)
        {
            var methods = grainInterfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            foreach (var method in methods)
            {
                if (!method.ContainsGenericParameters) continue;
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
                if (method.GetGenericArguments().Length != typeParameterCount) continue;

                return method;
            }

            throw new ArgumentException(
                $"Could not find generic method {methodName} on type {grainInterfaceType} with {typeParameterCount} type parameters.");
        }
    }
}
