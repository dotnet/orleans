using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Orleans.Serialization
{
    public class FieldUtils : IFieldUtils
    {
        /// <inheritdoc />
        public Delegate GetGetter(FieldInfo field)
        {
            return GetGetDelegate(
                field,
                typeof(Func<,>).MakeGenericType(field.DeclaringType, field.FieldType),
                new[] { field.DeclaringType });
        }

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
            if (declaringType == null)
            {
                throw new InvalidOperationException("Field " + field.Name + " does not have a declaring type.");
            }

            // Create a method to hold the generated IL.
            var method = new DynamicMethod(
                field.Name + "Get",
                field.FieldType,
                parameterTypes,
                typeof(FieldUtils).Module,
                true);

            // Emit IL to return the value of the Transaction property.
            var emitter = method.GetILGenerator();
            emitter.Emit(OpCodes.Ldarg_0);
            emitter.Emit(OpCodes.Ldfld, field);
            emitter.Emit(OpCodes.Ret);

            return method.CreateDelegate(delegateType);
        }

        /// <inheritdoc />
        public Delegate GetReferenceSetter(FieldInfo field)
        {
            var delegateType = typeof(Action<,>).MakeGenericType(field.DeclaringType, field.FieldType);
            return GetSetDelegate(field, delegateType, new[] { field.DeclaringType, field.FieldType });
        }

        /// <inheritdoc />
        public Delegate GetValueSetter(FieldInfo field)
        {
            var declaringType = field.DeclaringType;
            if (declaringType == null)
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
            if (declaringType == null)
            {
                throw new InvalidOperationException("Field " + field.Name + " does not have a declaring type.");
            }

            // Create a method to hold the generated IL.
            var method = new DynamicMethod(field.Name + "Set", null, parameterTypes, typeof(FieldUtils).Module, true);

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