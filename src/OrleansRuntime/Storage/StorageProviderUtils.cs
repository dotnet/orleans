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

﻿using System;
using System.Collections.Generic;
using System.Text;

using Orleans.Runtime;

namespace Orleans.Storage
{
    public class StorageProviderUtils
    {
        public static int PositiveHash(GrainReference grainReference, int hashRange)
        {
            int hash = unchecked((int)grainReference.GetUniformHashCode());
            int positiveHash = ((hash % hashRange) + hashRange) % hashRange;
            return positiveHash;
        }
        public static int PositiveHash(int hash, int hashRange)
        {
            int positiveHash = ((hash % hashRange) + hashRange) % hashRange;
            return positiveHash;
        }

        public static string PrintKeys(IEnumerable<Tuple<string, string>> keys)
        {
            return Utils.EnumerableToString(keys,
                keyTuple => string.Format("Key:{0}={1}", keyTuple.Item1, keyTuple.Item2 ?? "null"));
        }

        public static string PrintData(IDictionary<string, object> data)
        {
            if (data == null || data.Count == 0)
            {
                return "[ ]";
            }
            return Utils.EnumerableToString(data.Keys,
                key =>
                {
                    string val;
                    object obj = data[key];
#if DEBUG
                    // Show types
                    if (obj == null) 
                        val = "null";
                    else if (obj is string) 
                        val = "\"" + obj + "\"";
                    else 
                        val = obj.ToString() + " (" + obj.GetType() + ")";
#else
                    val = obj != null ? obj.ToString() : "null";
#endif
                    return string.Format("{0} => {1}", key, val);
                });
        }

        public static string PrintResults(IList<IDictionary<string, object>> results)
        {
            if (results == null || results.Count == 0)
                return "0 Results";
            
            var sb = new StringBuilder();
            sb.Append(results.Count).Append(" Results= ( ").AppendLine();
            foreach (var data in results)
                sb.Append("[ ").Append(PrintData(data)).Append(" ] ").AppendLine();
            
            sb.Append(")");
            return sb.ToString();
        }

        public static string PrintOneWrite(
            IEnumerable<Tuple<string, string>> keys,
            IDictionary<string, object> data,
            string eTag)
        {
            var sb = new StringBuilder();
            sb.Append("Keys=").Append(PrintKeys(keys));
            sb.Append(" Data=").Append(PrintData(data));
            sb.Append(" Etag=").Append(eTag ?? "null");
            return sb.ToString();
        }
    }
}