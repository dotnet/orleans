using System;

namespace Orleans.Configuration
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CollectionAgeLimitAttribute : Attribute
    {
        private double minutes = CollectionAgeLimitConstants.DefaultCollectionAgeLimitInMinutes;

        public double Minutes
        {
            get
            {
                return minutes;
            }
            set
            {
                this.minutes = value;
            }
        }
    }
}
