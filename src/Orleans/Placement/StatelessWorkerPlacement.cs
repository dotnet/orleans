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

namespace Orleans.Runtime
{
    [Serializable]
    internal class StatelessWorkerPlacement : PlacementStrategy
    {
        private static int defaultMaxActivationBankSize = Environment.ProcessorCount;

        public int MinAvailable { get; private set; }

        public int MaxLocal { get; private set; }

        internal static void InitializeClass(int defMaxActivationBankSize)
        {
            if (defMaxActivationBankSize < 1)
                throw new ArgumentOutOfRangeException("defMaxActivationBankSize",
                    "defMaxActivationBankSize must contain a value greater than zero.");
            
            defaultMaxActivationBankSize = defMaxActivationBankSize;
        }

        internal StatelessWorkerPlacement(int minAvailable, int defaultMaxLocal = -1)
        {
            MinAvailable = minAvailable;
            MaxLocal = defaultMaxLocal > 0 ? defaultMaxLocal : defaultMaxActivationBankSize;
        }

        public override string ToString()
        {
            return String.Format("StatelessWorkerPlacement(min={0}, max={1})", MinAvailable, MaxLocal);
        }

        public override bool Equals(object obj)
        {
            var other = obj as StatelessWorkerPlacement;
            return other != null && MinAvailable == other.MinAvailable && MaxLocal == other.MaxLocal;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode() + MinAvailable.GetHashCode() + MaxLocal.GetHashCode();
        }
    }

}
