using Orleans.Runtime.Configuration;
using System;

namespace Orleans
{
    /// <summary>
    /// When applied to a property on an options class, this attribute prevents the property value from being formatted by conforming <see cref="IOptionFormatter"/> instances.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class RedactAttribute : Attribute
    {
        /// <summary>
        /// Redacts the provided value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The redacted value.</returns>
        public virtual string Redact(object value)
        {
            return "REDACTED";
        }
    }

    /// <summary>
    /// When applied to a connection string property on an options class, this attribute prevents the property value from being formatted by conforming <see cref="IOptionFormatter"/> instances.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class RedactConnectionStringAttribute : RedactAttribute
    {
        /// <summary>
        /// Redacts the provided value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The redacted value.</returns>
        public override string Redact(object value)
        {
            return ConfigUtilities.RedactConnectionStringInfo(value as string);
        }
    }
}
