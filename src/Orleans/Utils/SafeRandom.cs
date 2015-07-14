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
    /// <summary>
    /// Thread-safe random number generator.
    /// Has same API as System.Random but takes a lock.
    /// </summary>
    internal class SafeRandom
    {
        private readonly Random random;

        public SafeRandom()
        {
            random = new Random();
        }

        public SafeRandom(int seed)
        {
            random = new Random(seed);
        }

        public int Next()
        {
            lock (random)
            {
                return random.Next();
            }
        }

        public int Next(int maxValue)
        {
            lock (random)
            {
                return random.Next(maxValue);
            }
        }

        public int Next(int minValue, int maxValue)
        {
            lock (random)
            {
                return random.Next(minValue, maxValue);
            }
        }

        public void NextBytes(byte[] buffer)
        {
            lock (random)
            {
                random.NextBytes(buffer);
            }
        }

        public double NextDouble()
        {
            lock (random)
            {
                return random.NextDouble();
            }
        }
    }
}
