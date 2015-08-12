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
    internal class ActivationAddress
    {
        public GrainId Grain { get; private set; }
        public ActivationId Activation { get; private set; }
        public SiloAddress Silo { get; private set; }

        public bool IsComplete
        {
            get { return Grain != null && Activation != null && Silo != null; }
        }

        private ActivationAddress(SiloAddress silo, GrainId grain, ActivationId activation)
        {
            Silo = silo;
            Grain = grain;
            Activation = activation;
        }

        public static ActivationAddress NewActivationAddress(SiloAddress silo, GrainId grain)
        {
            var activation = ActivationId.NewId();
            return GetAddress(silo, grain, activation);
        }

        public static ActivationAddress GetAddress(SiloAddress silo, GrainId grain, ActivationId activation)
        {
            // Silo part is not mandatory
            if (grain == null) throw new ArgumentNullException("grain");

            return new ActivationAddress(silo, grain, activation);
        }

        public override bool Equals(object obj)
        {
            var other = obj as ActivationAddress;
            return other != null && Equals(Silo, other.Silo) && Equals(Grain, other.Grain) && Equals(Activation, other.Activation);
        }

        public override int GetHashCode()
        {
            return (Silo != null ? Silo.GetHashCode() : 0) ^
                (Grain != null ? Grain.GetHashCode() : 0) ^
                (Activation != null ? Activation.GetHashCode() : 0);
        }

        public override string ToString()
        {
            return String.Format("{0}{1}{2}", Silo, Grain, Activation);
        }

        public string ToFullString()
        {
            return
                String.Format(
                    "[ActivationAddress: {0}, Full GrainId: {1}, Full ActivationId: {2}]",
                    this.ToString(),                        // 0
                    this.Grain.ToFullString(),              // 1
                    this.Activation.ToFullString());        // 2
        }

        public bool Matches(ActivationAddress other)
        {
            return Equals(Grain, other.Grain) && Equals(Activation, other.Activation);
        }
    }
}
