using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Orleans.Streams
{
    /// <summary>
    /// This class is a [Serializable] holder for a logical-or composite predicate function.
    /// </summary>
    [Serializable]
    internal class OrFilter : IStreamFilterPredicateWrapper, ISerializable
    {
        public object FilterData
        {
            get
            {
                // This FilterData field is only evey passed in to our own ShouldReceive function below, 
                // which does not actually use it for anything.
                // Underlying filters are passed their own FilterData objects which were passed in 
                // when the original Subscribe call was made.
                return null;
            }
        }

        private readonly List<IStreamFilterPredicateWrapper> filters; // Serializable func info

        [NonSerialized]
        private static readonly Type serializedType = typeof(List<IStreamFilterPredicateWrapper>);

        public OrFilter(IStreamFilterPredicateWrapper filter1, IStreamFilterPredicateWrapper filter2)
        {
            filters = new List<IStreamFilterPredicateWrapper> { filter1, filter2 };
        }

        public void AddFilter(IStreamFilterPredicateWrapper filter)
        {
            filters.Add(filter);
        }

        #region ISerializable methods
        protected OrFilter(SerializationInfo info, StreamingContext context)
        {
            filters = (List<IStreamFilterPredicateWrapper>)info.GetValue("Filters", serializedType);
        }
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Filters", this.filters, serializedType);
        }
        #endregion

        public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
        {
            if (filters == null || filters.Count == 0) return true;

            foreach (var filter in filters)
            {
                if (filter.ShouldReceive(stream, filter.FilterData, item))
                    return true; // We got the answer for logical-or predicate
            }

            return false; // Everybody said 'no'
        }
    }
}
