using System;
using System.Reflection;

namespace Orleans.Serialization
{
    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    /// <param name="value">The value being set.</param>
    public delegate void ValueTypeSetter<TDeclaring, in TField>(ref TDeclaring instance, TField value);

    public interface IFieldUtils
    {
        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <returns>A delegate to get the value of a specified field.</returns>
        Delegate GetGetter(FieldInfo field);

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        Delegate GetReferenceSetter(FieldInfo field);

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        Delegate GetValueSetter(FieldInfo field);
    }
}