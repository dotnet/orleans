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
    /// Data class encapsulating details of a particular system limit.
    /// </summary>
    [Serializable]
    public class LimitValue
    {
        /// <summary>
        /// Name of this Limit value
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// 'Soft" limit threshold value for this Limit, after which Warnings will start to be generated
        /// </summary>
        public int SoftLimitThreshold { get; set; }
        /// <summary>
        /// 'Hard' limit threshold value, after which Errors will start to be generated and action take (for example, rejecting new request messages, etc) 
        /// to actively reduce the limit value back to within thresholds.
        /// </summary>
        public int HardLimitThreshold { get; set; }

        public override string ToString()
        {
            return string.Format("Limit:{0},SoftLimitThreshold={1},HardLimitThreshold={2}",
                Name, SoftLimitThreshold, HardLimitThreshold);
        }
    }
}
