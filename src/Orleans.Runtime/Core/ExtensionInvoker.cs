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
    internal class ExtensionInvoker : IGrainMethodInvoker, IGrainExtensionMap
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
        public bool Remove(IGrainExtension extension)
        {
            int interfaceId = 0;

            foreach (var kv in extensionMap)
                if (kv.Value.Item1 == extension)
                {
                    interfaceId = kv.Key;
                    break;
                }

            if (interfaceId == 0) // not found
                throw new InvalidOperationException(String.Format("Extension {0} is not installed",
                    extension.GetType().FullName));

            extensionMap.Remove(interfaceId);
            return extensionMap.Count == 0;
        }

        public bool TryGetExtensionHandler(Type extensionType, out IGrainExtension result)
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
        /// <param name="request"></param>
        /// <returns></returns>
        public Task<object> Invoke(IAddressable grain, InvokeMethodRequest request)
        {
            if (extensionMap == null || !extensionMap.TryGetValue(request.InterfaceId, out var value))
                throw new InvalidOperationException(
                    String.Format("Extension invoker invoked with an unknown interface ID:{0}.", request.InterfaceId));

            var invoker = value.Item2;
            var extension = value.Item1;
            return invoker.Invoke(extension, request);
        }

        public bool IsExtensionInstalled(int interfaceId)
        {
            return extensionMap != null && extensionMap.ContainsKey(interfaceId);
        }

        public int InterfaceId
        {
            get { return 0; } // 0 indicates an extension invoker that may have multiple intefaces inplemented by extensions.
        }

        public ushort InterfaceVersion
        {
            get { return 0; }
        }

        /// <summary>
        /// Gets the extension from this instance if it is available.
        /// </summary>
        /// <param name="interfaceId">The interface id.</param>
        /// <param name="extension">The extension.</param>
        /// <returns>
        /// <see langword="true"/> if the extension is found, <see langword="false"/> otherwise.
        /// </returns>
        public bool TryGetExtension(int interfaceId, out IGrainExtension extension)
        {
            if (extensionMap != null && extensionMap.TryGetValue(interfaceId, out var value))
            {
                extension = value.Item1;
            }
            else
            {
                extension = null;
            }

            return extension != null;
        }
    }
}
