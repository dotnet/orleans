using System;

namespace Orleans.Configuration
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CollectionAgeLimitAttribute : Attribute
    {
        public double Minutes { get; set; }
    }
}
