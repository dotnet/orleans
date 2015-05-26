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

using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Bridge to provide runtime services to Orleans clients, both inside and outside silos.
    /// </summary>
    /// <remarks>
    /// Only one RuntimeClient is permitted per AppDomain.
    /// </remarks>
    internal static class RuntimeClient
    {
        /// <summary>
        /// A reference to the RuntimeClient instance in the current app domain, 
        /// of the appropriate type depending on whether caller is running inside or outside silo.
        /// </summary>
        internal static IRuntimeClient Current { get; set; }

        internal static Message CreateMessage(InvokeMethodRequest request, InvokeMethodOptions options)
        {
            var message = new Message(
                Message.Categories.Application,
                (options & InvokeMethodOptions.OneWay) != 0 ? Message.Directions.OneWay : Message.Directions.Request)
            {
                Id = CorrelationId.GetNext(),
                InterfaceId = request.InterfaceId,
                MethodId = request.MethodId,
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                BodyObject = request
            };
            
            if ((options & InvokeMethodOptions.AlwaysInterleave) != 0)
                message.IsAlwaysInterleave = true;

            RequestContext.ExportToMessage(message);
            return message;
        }
    }
}
