/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Orleans.Streams
{
    /// <summary>
    /// This class is a [Serializable] holder for a logical-or composite predicate function.
    /// </summary>
    [Serializable]
    internal class OrFilter : IStreamFilterPredicateWrapper
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
