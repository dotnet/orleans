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
    public delegate TField ValueTypeGetter<TDeclaring, out TField>(ref TDeclaring instance) where TDeclaring : struct;

    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    /// <param name="value">The value being set.</param>
    public delegate void ValueTypeSetter<TDeclaring, in TField>(ref TDeclaring instance, TField value) where TDeclaring : struct;

    /// <summary>
    /// Functionality for accessing fields.
    /// </summary>
    public static class FieldAccessor
    {
        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <param name="field">
        /// The field.
        /// </param>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static Delegate GetGetter(FieldInfo field) => GetGetter(field, false);

        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <param name="field">
        /// The field.
        /// </param>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static Delegate GetValueGetter(FieldInfo field) => GetGetter(field, true);

        private static Delegate GetGetter(FieldInfo field, bool byref)
        {
            var declaringType = field.DeclaringType ?? throw new InvalidOperationException($"Field {field.Name} does not have a declaring type.");
            var parameterTypes = new[] { typeof(object), byref ? declaringType.MakeByRefType() : declaringType };

            var method = new DynamicMethod(field.Name + "Get", field.FieldType, parameterTypes, typeof(FieldAccessor).Module, true);

            var emitter = method.GetILGenerator();
            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Ldfld, field);
            emitter.Emit(OpCodes.Ret);

            return method.CreateDelegate((byref ? typeof(ValueTypeGetter<,>) : typeof(Func<,>)).MakeGenericType(declaringType, field.FieldType));
        }

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static Delegate GetReferenceSetter(FieldInfo field) => GetSetter(field, false);

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static Delegate GetValueSetter(FieldInfo field) => GetSetter(field, true);

        private static Delegate GetSetter(FieldInfo field, bool byref)
        {
            var declaringType = field.DeclaringType ?? throw new InvalidOperationException($"Field {field.Name} does not have a declaring type.");
            var parameterTypes = new[] { typeof(object), byref ? declaringType.MakeByRefType() : declaringType, field.FieldType };

            var method = new DynamicMethod(field.Name + "Set", null, parameterTypes, typeof(FieldAccessor).Module, true);

            var emitter = method.GetILGenerator();
            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Ldarg_2);
            emitter.Emit(OpCodes.Stfld, field);
            emitter.Emit(OpCodes.Ret);

            return method.CreateDelegate((byref ? typeof(ValueTypeSetter<,>) : typeof(Action<,>)).MakeGenericType(declaringType, field.FieldType));
        }
    }
}