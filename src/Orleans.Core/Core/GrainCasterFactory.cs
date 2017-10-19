namespace Orleans
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;

    using Orleans.Runtime;

    internal static class GrainCasterFactory
    {
        /// <summary>
        /// The cached <see cref="MethodInfo"/> for <see cref="GrainExtensions.AsWeaklyTypedReference"/>.
        /// </summary>
        private static readonly MethodInfo GrainReferenceCastHelperMethodInfo =
            TypeUtils.Method((IAddressable i) => i.AsWeaklyTypedReference());

        /// <summary>
        /// The cached <see cref="MethodInfo"/> for checking if one type is assignable from another.
        /// </summary>
        private static readonly MethodInfo IsAssignableFromMethodInfo =
            TypeUtils.Method((Type t) => t.IsAssignableFrom(default(Type)));

        /// <summary>
        /// The cached <see cref="MethodInfo"/> for <see cref="object.GetType"/>.
        /// </summary>
        private static readonly MethodInfo GetTypeMethodInfo = TypeUtils.Method((object o) => o.GetType());

        /// <summary>
        /// The cached <see cref="MethodInfo"/> for <see cref="Type.GetTypeFromHandle"/>.
        /// </summary>
        private static readonly MethodInfo GetTypeFromHandleMethodInfo =
            TypeUtils.Method(() => Type.GetTypeFromHandle(default(RuntimeTypeHandle)));

        /// <summary>
        /// Creates a grain reference caster delegate for the provided grain interface type and concrete grain reference type.
        /// </summary>
        /// <param name="interfaceType">The interface type.</param>
        /// <param name="grainReferenceType">The grain reference implementation type.</param>
        /// <returns>A grain reference caster delegate.</returns>
        public static GrainFactory.GrainReferenceCaster CreateGrainReferenceCaster(
            Type interfaceType,
            Type grainReferenceType)
        {
            // Get the grain reference constructor.
            var constructor =
                grainReferenceType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                  .Where(IsGrainReferenceCopyConstructor)
                                  .FirstOrDefault();

            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Cannot find suitable constructor on generated reference type for interface '{interfaceType}'");
            }

            var method = new DynamicMethod(
                "caster_" + grainReferenceType.Name,
                typeof(object),
                new[] { typeof(IAddressable) },
                typeof(GrainFactory).GetTypeInfo().Module,
                true);
            var il = method.GetILGenerator();
            var returnLabel = il.DefineLabel();

            // C#: object result = grainRef;
            il.DeclareLocal(typeof(object));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stloc_0);

            // Get the runtime value of the target grain reference type.
            // C#: var grainReferenceType = Type.GetTypeFromHandle(<grainReferenceType>.TypeHandle);
            il.DeclareLocal(typeof(Type));
            il.Emit(OpCodes.Ldtoken, grainReferenceType);
            EmitCall(il, GetTypeFromHandleMethodInfo);
            il.Emit(OpCodes.Stloc_1);

            // Get the runtime value of the target interface type.
            // C#: var interfaceType = Type.GetTypeFromHandle(<interfaceType>.TypeHandle);
            il.DeclareLocal(typeof(Type));
            il.Emit(OpCodes.Ldtoken, interfaceType);
            EmitCall(il, GetTypeFromHandleMethodInfo);
            il.Emit(OpCodes.Stloc_2);

            // C#: if (grainReferenceType.IsAssignableFrom(grainRef.GetType())) return grainRef;
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Call, GetTypeMethodInfo);
            EmitCall(il, IsAssignableFromMethodInfo);
            il.Emit(OpCodes.Brtrue_S, returnLabel);

            // Convert the grainRef parameter to a weakly typed GrainReference.
            // C#: result = grainRef.AsWeaklyTypedReference();
            il.Emit(OpCodes.Ldloc_0);
            EmitCall(il, GrainReferenceCastHelperMethodInfo);
            il.Emit(OpCodes.Stloc_0);

            // If the result is assignable to the target interface type, return it.
            // C#: if (interfaceType.IsAssignableFrom(result.GetType())) return result;
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Call, GetTypeMethodInfo);
            EmitCall(il, IsAssignableFromMethodInfo);
            il.Emit(OpCodes.Brtrue_S, returnLabel);

            // Otherwise, cast the input to a GrainReference and wrap it in the target type by calling the copy
            // constructor.
            // C#: result = new <grainReferenceType>((GrainReference)result);
            var grainRefConstructor = TypeUtils.GetConstructorThatMatches(
                grainReferenceType,
                new[] { typeof(GrainReference) });
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Castclass, typeof(GrainReference));
            il.Emit(OpCodes.Newobj, grainRefConstructor);
            il.Emit(OpCodes.Stloc_0);

            // C#: return result;
            il.MarkLabel(returnLabel);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ret);

            return (GrainFactory.GrainReferenceCaster)method.CreateDelegate(typeof(GrainFactory.GrainReferenceCaster));
        }
        
        /// <summary>
        /// Emits a call to the specified method.
        /// </summary>
        /// <param name="il">The il generator.</param>
        /// <param name="method">The method to call.</param>
        private static void EmitCall(ILGenerator il, MethodInfo method)
        {
            if (method.IsFinal || !method.IsVirtual)
            {
                il.Emit(OpCodes.Call, method);
            }
            else
            {
                il.Emit(OpCodes.Callvirt, method);
            }
        }

        private static bool IsGrainReferenceCopyConstructor(ConstructorInfo constructor)
        {
            var parameters = constructor.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(GrainReference);
        }
    }
}