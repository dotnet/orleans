using System;
using System.Collections.Generic;
using System.Text;

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
                if (value <= 0d)
                {
                    throw new ArgumentOutOfRangeException("Collection Age Limit must be a positive number.");
                }
                this.minutes = value;
            }
        }
    }
}
