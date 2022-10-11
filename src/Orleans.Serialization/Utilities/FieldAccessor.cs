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
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static Delegate GetGetter(Type declaringType, string fieldName) => GetGetter(declaringType, fieldName, false);

        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static Delegate GetValueGetter(Type declaringType, string fieldName) => GetGetter(declaringType, fieldName, true);

        private static Delegate GetGetter(Type declaringType, string fieldName, bool byref)
        {
            var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var parameterTypes = new[] { typeof(object), byref ? declaringType.MakeByRefType() : declaringType };

            var method = new DynamicMethod(fieldName + "Get", field.FieldType, parameterTypes, typeof(FieldAccessor).Module, true);

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
        public static Delegate GetReferenceSetter(Type declaringType, string fieldName) => GetSetter(declaringType, fieldName, false);

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static Delegate GetValueSetter(Type declaringType, string fieldName) => GetSetter(declaringType, fieldName, true);

        private static Delegate GetSetter(Type declaringType, string fieldName, bool byref)
        {
            var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var parameterTypes = new[] { typeof(object), byref ? declaringType.MakeByRefType() : declaringType, field.FieldType };

            var method = new DynamicMethod(fieldName + "Set", null, parameterTypes, typeof(FieldAccessor).Module, true);

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