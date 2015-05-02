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
using System.Reflection;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public delegate bool StreamFilterPredicate(IStreamIdentity stream, object filterData, object item);

    /// <summary>
    /// This class is a [Serializable] function pointer to a static predicate method, used for stream filtering.
    /// </summary>
    [Serializable]
    internal class FilterPredicateWrapperData : IStreamFilterPredicateWrapper
    {
        public object FilterData { get; private set; }

        private string methodName;
        private string className;

        [NonSerialized]
        private StreamFilterPredicate predicateFunc;

        internal FilterPredicateWrapperData(object filterData, StreamFilterPredicate pred)
        {
            FilterData = filterData;
            predicateFunc = pred;
            DehydrateStaticFunc(pred);
        }

        #region ISerializable methods
        protected FilterPredicateWrapperData(SerializationInfo info, StreamingContext context)
        {
            FilterData = info.GetValue("FilterData", typeof(object));
            methodName = info.GetString("MethodName");
            className = info.GetString("ClassName");
            predicateFunc = RehydrateStaticFuncion(className, methodName);
        }
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("FilterData", FilterData);
            info.AddValue("MethodName", methodName);
            info.AddValue("ClassName", className);
        }
        #endregion

        public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
        {
            if (predicateFunc == null)
            {
                predicateFunc = RehydrateStaticFuncion(className, methodName);
            }
            return predicateFunc(stream, filterData, item);
        }

        private static StreamFilterPredicate RehydrateStaticFuncion(string funcClassName, string funcMethodName)
        {
            var funcClassType = CachedTypeResolver.Instance.ResolveType(funcClassName);
            var method = funcClassType.GetMethod(funcMethodName);
            return (StreamFilterPredicate)method.CreateDelegate(typeof(StreamFilterPredicate));
        }

        private void DehydrateStaticFunc(StreamFilterPredicate pred)
        {
            var method = pred.Method;

            if (!CheckStaticFunc(method)) throw new ArgumentException("Filter function must be static and not abstract.");

            className = method.DeclaringType.FullName;
            methodName = method.Name;
        }

        private static bool CheckStaticFunc(MethodInfo method)
        {
            return method.IsStatic && !method.IsAbstract;
        }

        public override string ToString()
        {
            return string.Format("StreamFilterFunction:Class={0},Method={1}", className, methodName);
        }
    }
}
