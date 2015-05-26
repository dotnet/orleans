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

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Limits Manager
    /// </summary>
    internal static class LimitManager
    {
        private static ILimitsConfiguration limitsConfig;

        public static void Initialize(ILimitsConfiguration limitValues)
        {
            limitsConfig = limitValues;
        }

        public static void UnInitialize()
        {
            limitsConfig = null;
        }

        public static LimitValue GetLimit(string name)
        {
            if (limitsConfig == null) throw new InvalidOperationException("LimitsManager not yet initialized");

            return GetLimit(name, 0, 0);
        }

        public static LimitValue GetLimit(string name, int defaultSoftLimit)
        {
            if (limitsConfig == null) throw new InvalidOperationException("LimitsManager not yet initialized");

            return GetLimit(name, defaultSoftLimit, 0);
        }

        public static LimitValue GetLimit(string name, int defaultSoftLimit, int defaultHardLimit)
        {
            if (limitsConfig == null) throw new InvalidOperationException("LimitsManager not yet initialized");

            return limitsConfig.GetLimit(name) ?? 
                new LimitValue { Name = name, SoftLimitThreshold = defaultSoftLimit, HardLimitThreshold = defaultHardLimit};
        }
    }
}
