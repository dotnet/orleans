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

using Orleans.Runtime;

using Orleans.CodeGeneration;

namespace Orleans
{
    /// <summary>
    /// This is a markup interface for system targets.
    /// System target are internal runtime objects that share some behaivior with grains, but also impose certain restrictions. In particular:
    /// System target are asynchronusly addressable actors.
    /// Proxy class is being generated for ISystemTarget, just like for IGrain
    /// System target are scheduled by the runtime scheduler and follow turn based concurrency.
    /// </summary> 
    internal interface ISystemTarget : IAddressable
    {
    }

    /// <summary>
    /// Internal interface implemented by SystemTarget classes to expose the necessary internal info that allows this.AsReference to for for SystemTarget's same as it does for a grain class.
    /// </summary>
    internal interface ISystemTargetBase
    {
        SiloAddress Silo { get; }
        GrainId GrainId { get; }
    }

    // Common internal interface for SystemTarget and ActivationData.
    internal interface IInvokable
    {
        IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null);
    }
}
