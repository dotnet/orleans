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
    // Used for Client -> gateway and Silo <-> Silo messeging
    // Message implements ITimeInterval to be able to measure different time intervals in the lifecycle of a message,
    // such as time in queue...
    internal interface IOutgoingMessage : ITimeInterval
    {
        bool IsSameDestination(IOutgoingMessage other);
    }

    // Used for gateway -> Client messaging
    internal class OutgoingClientMessage : Tuple<GrainId, Message>, IOutgoingMessage
    {
        public OutgoingClientMessage(GrainId clientId, Message message)
            : base(clientId, message)
        {
        }

        public bool IsSameDestination(IOutgoingMessage other)
        {
            var otherTuple = (OutgoingClientMessage)other;
            return otherTuple != null && this.Item1.Equals(otherTuple.Item1);
        }

        public void Start()
        {
            this.Item2.Start();
        }

        public void Stop()
        {
            this.Item2.Stop();
        }

        public void Restart()
        {
            this.Item2.Start();
        }

        public TimeSpan Elapsed
        {
            get { return this.Item2.Elapsed; }
        }
    }
}
