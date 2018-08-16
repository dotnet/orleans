using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CollectionAgeLimitAttribute : Attribute
    {
        private TimeSpan time = CollectionAgeLimitConstants.DefaultCollectionAgeLimit;

        public TimeSpan Age { get; set; }

        public CollectionAgeLimitAttribute(TimeSpan Age) { }
        //{
        //    get
        //    {
        //        return time;
        //    }
        //    set
        //    {
        //        if (value <= TimeSpan.Zero)
        //        {
        //            throw new ArgumentOutOfRangeException("Collection Age Limit must be a positive number.");
        //        }
        //        time = value;
        //    }
        //}

        //public CollectionAgeLimitAttribute(TimeSpan time)
        //{
        //    this.time = time;
        //}
    }
}
