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
using System.Linq;
using System.Text;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// For internal (run-time) use only.
    /// Base class of all the activation attributes 
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1813:AvoidUnsealedAttributes"), AttributeUsage(System.AttributeTargets.All)]
    public abstract class GeneratedAttribute : Attribute
    {
        /// <summary>
        /// Type for which this activation is implemented
        /// </summary>
        public string ForGrainType { get; protected set; }

        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        protected GeneratedAttribute(string forGrainType)
        {
            ForGrainType = forGrainType;
        }
        /// <summary>
        /// </summary>
        protected GeneratedAttribute() { }
    }
    
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class GrainStateAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public GrainStateAttribute(string forGrainType)
        {
            ForGrainType = forGrainType;
        }
    }
    
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class MethodInvokerAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public MethodInvokerAttribute(string forGrainType, int interfaceId)
        {
            ForGrainType = forGrainType;
            InterfaceId = interfaceId;
        }

        public int InterfaceId { get; private set; }
    }
    
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class GrainReferenceAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public GrainReferenceAttribute(string forGrainType)
        {
            ForGrainType = forGrainType;
        }
    }
}
