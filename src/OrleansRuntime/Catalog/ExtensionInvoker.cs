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
using System.Threading.Tasks;
using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    // This class is used for activations that have extension invokers. It keeps a dictionary of 
    // invoker objects to use with the activation, and extend the default invoker
    // defined for the grain class.
    // Note that in all cases we never have more than one copy of an actual invoker;
    // we may have a ExtensionInvoker per activation, in the worst case.
    internal class ExtensionInvoker : IGrainMethodInvoker
    {
        // Because calls to ExtensionInvoker are allways made within the activation context,
        // we rely on the single-threading guarantee of the runtime and do not protect the map with a lock.
        private Dictionary<int, Tuple<IGrainExtension, IGrainExtensionMethodInvoker>> extensionMap; // key is the extension interface ID

        /// <summary>
        /// Try to add an extension for the specific interface ID.
        /// Fail and return false if there is already an extension for that interface ID.
        /// Note that if an extension invoker handles multiple interface IDs, it can only be associated
        /// with one of those IDs when added, and so only conflicts on that one ID will be detected and prevented.
        /// </summary>
        /// <param name="invoker"></param>
        /// <param name="handler"></param>
        /// <returns></returns>
        internal bool TryAddExtension(IGrainExtensionMethodInvoker invoker, IGrainExtension handler)
        {
            if (extensionMap == null)
            {
                extensionMap = new Dictionary<int, Tuple<IGrainExtension, IGrainExtensionMethodInvoker>>(1);
            }

            if (extensionMap.ContainsKey(invoker.InterfaceId)) return false;

            extensionMap.Add(invoker.InterfaceId, new Tuple<IGrainExtension, IGrainExtensionMethodInvoker>(handler, invoker));
            return true;
        }

        /// <summary>
        /// Removes all extensions for the specified interface id.
        /// Returns true if the chained invoker no longer has any extensions and may be safely retired.
        /// </summary>
        /// <param name="extension"></param>
        /// <returns>true if the chained invoker is now empty, false otherwise</returns>
        internal bool Remove(IGrainExtension extension)
        {
            int interfaceId = 0;

            foreach (int iface in extensionMap.Keys)
                if (extensionMap[iface].Item1 == extension)
                {
                    interfaceId = iface;
                    break;
                }

            if (interfaceId == 0) // not found
                throw new InvalidOperationException(String.Format("Extension {0} is not installed",
                    extension.GetType().FullName));

            extensionMap.Remove(interfaceId);
            return extensionMap.Count == 0;
        }

        internal bool TryGetExtensionHandler(Type extensionType, out IGrainExtension result)
        {
            result = null;

            if (extensionMap == null) return false;

            foreach (var ext in extensionMap.Values)
                if (extensionType == ext.Item1.GetType())
                {
                    result = ext.Item1;
                    return true;
                }

            return false;
        }

        /// <summary>
        /// Invokes the appropriate grain or extension method for the request interface ID and method ID.
        /// First each extension invoker is tried; if no extension handles the request, then the base
        /// invoker is used to handle the request.
        /// The base invoker will throw an appropriate exception if the request is not recognized.
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="interfaceId"></param>
        /// <param name="methodId"></param>
        /// <param name="arguments"></param>
        /// <returns></returns>
        public Task<object> Invoke(IAddressable grain, int interfaceId, int methodId, object[] arguments)
        {
            if (extensionMap == null || !extensionMap.ContainsKey(interfaceId))
                throw new InvalidOperationException(
                    String.Format("Extension invoker invoked with an unknown inteface ID:{0}.", interfaceId));

            var invoker = extensionMap[interfaceId].Item2;
            var extension = extensionMap[interfaceId].Item1;
            return invoker.Invoke(extension, interfaceId, methodId, arguments);
        }

        internal bool IsExtensionInstalled(int interfaceId)
        {
            return extensionMap != null && extensionMap.ContainsKey(interfaceId);
        }

        public int InterfaceId
        {
            get { return 0; } // 0 indicates an extension invoker that may have multiple intefaces inplemented by extensions.
        }
    }
}
