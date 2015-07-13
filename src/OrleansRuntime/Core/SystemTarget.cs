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
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal abstract class SystemTarget : ISystemTarget, ISystemTargetBase, IInvokable, IStreamable
    {
        private IGrainMethodInvoker lastInvoker;
        private ExtensionInvoker extensionInvoker;
        private readonly SchedulingContext schedulingContext;
        
        protected SystemTarget(GrainId grainId, SiloAddress silo) 
            : this(grainId, silo, false)
        {
        }

        protected SystemTarget(GrainId grainId, SiloAddress silo, bool lowPriority)
        {
            GrainId = grainId;
            Silo = silo;
            ActivationId = ActivationId.GetSystemActivation(grainId, silo);
            schedulingContext = new SchedulingContext(this, lowPriority);
        }

        public SiloAddress Silo { get; private set; }
        public GrainId GrainId { get; private set; }
        public ActivationId ActivationId { get; set; }

        internal SchedulingContext SchedulingContext { get { return schedulingContext; } }

        #region Method invocation

        public IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null)
        {
            return ActivationData.GetInvoker(ref lastInvoker, ref extensionInvoker, interfaceId, genericGrainType);
        }

        public bool TryAddExtension(IGrainExtensionMethodInvoker invoker, IGrainExtension extension)
        {
            if(extensionInvoker == null)
                extensionInvoker = new ExtensionInvoker();

            return extensionInvoker.TryAddExtension(invoker, extension);
        }

        public void RemoveExtension(IGrainExtension extension)
        {
            if (extensionInvoker != null)
            {
                if (extensionInvoker.Remove(extension))
                    extensionInvoker = null;
            }
            else
                throw new InvalidOperationException("Grain extensions not installed.");
        }

        public bool TryGetExtensionHandler(Type extensionType, out IGrainExtension result)
        {
            result = null;
            return extensionInvoker != null && extensionInvoker.TryGetExtensionHandler(extensionType, out result);
        }

        #endregion

        private Streams.StreamDirectory streamDirectory;
        public Streams.StreamDirectory GetStreamDirectory()
        {
            return streamDirectory ?? (streamDirectory = new Streams.StreamDirectory());
        }

        public bool IsUsingStreams
        {
            get { return streamDirectory != null; }
        }

        public async Task DeactivateStreamResources()
        {
            if (streamDirectory == null) return; // No streams - Nothing to do.
            if (extensionInvoker == null) return; // No installed extensions - Nothing to do.
            await streamDirectory.Cleanup();
        }

        public void HandleNewRequest(Message request)
        {
            InsideRuntimeClient.Current.Invoke(this, this, request).Ignore();
        }

        public void HandleResponse(Message response)
        {
            RuntimeClient.Current.ReceiveResponse(response);
        }

        public override string ToString()
        {
            return String.Format("[{0}SystemTarget: {1}{2}{3}]",
                 SchedulingContext.IsSystemPriorityContext ? String.Empty : "LowPriority",
                 Silo,
                 GrainId,
                 ActivationId);
        }
    }
}
