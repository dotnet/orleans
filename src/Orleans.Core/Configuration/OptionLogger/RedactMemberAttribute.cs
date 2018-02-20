using Orleans.Runtime.Configuration;
using System;

namespace Orleans
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class RedactAttribute : Attribute
    {
        public virtual string Redact(object value)
        {
            return "REDACTED";
        }
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class RedactConnectionStringAttribute : RedactAttribute
    {
        public override string Redact(object value)
        {
            return ConfigUtilities.RedactConnectionStringInfo(value as string);
        }
    }
}
