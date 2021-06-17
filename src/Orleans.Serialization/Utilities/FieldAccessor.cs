using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Orleans.Serialization.Utilities
{
    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    /// <param name="value">The value being set.</param>
    public delegate void ValueTypeSetter<TDeclaring, in TField>(ref TDeclaring instance, TField value);

    public static class FieldAccessor
    {
        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static Delegate GetGetter(FieldInfo field) => GetGetDelegate(
                field,
                typeof(Func<,>).MakeGenericType(field.DeclaringType, field.FieldType),
                new[] { field.DeclaringType });

        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <param name="field">
        /// The field.
        /// </param>
        /// <param name="delegateType">The delegate type.</param>
        /// <param name="parameterTypes">The parameter types.</param>
        /// <returns>A delegate to get the value of a specified field.</returns>
        private static Delegate GetGetDelegate(FieldInfo field, Type delegateType, Type[] parameterTypes)
        {
            var declaringType = field.DeclaringType;
            if (declaringType is null)
            {
                throw new InvalidOperationException("Field " + field.Name + " does not have a declaring type.");
            }

            // Create a method to hold the generated IL.
            var method = new DynamicMethod(
                field.Name + "Get",
                field.FieldType,
                parameterTypes,
                typeof(FieldAccessor).Module,
                true);

            // Emit IL to return the value of the Transaction property.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Ldfld, field);
            emitter.Emit(OpCodes.Ret);

            return method.CreateDelegate(delegateType);
        }

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static Delegate GetReferenceSetter(FieldInfo field)
        {
            var delegateType = typeof(Action<,>).MakeGenericType(field.DeclaringType, field.FieldType);
            return GetSetDelegate(field, delegateType, new[] { field.DeclaringType, field.FieldType });
        }

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static Delegate GetValueSetter(FieldInfo field)
        {
            var declaringType = field.DeclaringType;
            if (declaringType is null)
            {
                throw new InvalidOperationException("Field " + field.Name + " does not have a declaring type.");
            }

            // Value types need to be passed by-ref.
            var parameterTypes = new[] { declaringType.MakeByRefType(), field.FieldType };
            var delegateType = typeof(ValueTypeSetter<,>).MakeGenericType(field.DeclaringType, field.FieldType);

            return GetSetDelegate(field, delegateType, parameterTypes);
        }

        /// <summary>
        /// Returns a delegate to set the value of a specified field.
        /// </summary>
        /// <param name="field">
        /// The field.
        /// </param>
        /// <param name="delegateType">The delegate type.</param>
        /// <param name="parameterTypes">The parameter types.</param>
        /// <returns>A delegate to set the value of a specified field.</returns>
        private static Delegate GetSetDelegate(FieldInfo field, Type delegateType, Type[] parameterTypes)
        {
            var declaringType = field.DeclaringType;
            if (declaringType is null)
            {
                throw new InvalidOperationException("Field " + field.Name + " does not have a declaring type.");
            }

            // Create a method to hold the generated IL.
            var method = new DynamicMethod(field.Name + "Set", null, parameterTypes, typeof(FieldAccessor).Module, true);

            // Emit IL to return the value of the Transaction property.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Stfld, field);
            emitter.Emit(OpCodes.Ret);

            return method.CreateDelegate(delegateType);
        }
    }
}