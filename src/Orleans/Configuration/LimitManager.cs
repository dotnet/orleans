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

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Limits Manager
    /// </summary>
    [Serializable]
    public class LimitManager
    {
        public IDictionary<string, LimitValue> LimitValues { get; private set; }

        public LimitManager()
        {
            LimitValues = new Dictionary<string, LimitValue>();
        }

        public LimitManager(LimitManager other)
        {
            LimitValues = new Dictionary<string, LimitValue>(other.LimitValues);
        }

        public void AddLimitValue(string name, LimitValue @value)
        {
            LimitValues.Add(name, @value);
        }

        public LimitValue GetLimit(string name)
        {
            return GetLimit(name, 0, 0);
        }

        public LimitValue GetLimit(string name, int defaultSoftLimit)
        {
            return GetLimit(name, defaultSoftLimit, 0);
        }

        public LimitValue GetLimit(string name, int defaultSoftLimit, int defaultHardLimit)
        {
            LimitValue limit;
            if(LimitValues.TryGetValue(name, out limit))
                return limit;

            return new LimitValue { Name = name, SoftLimitThreshold = defaultSoftLimit, HardLimitThreshold = defaultHardLimit };
        }

        public static LimitValue GetDefaultLimit(string name)
        {
            return new LimitValue { Name = name, SoftLimitThreshold = 0, HardLimitThreshold = 0 };
        }
    }
}
