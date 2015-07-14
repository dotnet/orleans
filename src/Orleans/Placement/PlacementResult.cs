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
    internal class PlacementResult
    {
        public PlacementStrategy PlacementStrategy { get; private set; }
        public bool IsNewPlacement { get { return PlacementStrategy != null; } }
        public ActivationId Activation { get; private set; }
        public SiloAddress Silo { get; private set; }
        /// <summary>
        /// Some storage providers need to know the grain type in order to read the state.
        /// The PlacementResult is generated based on the target grain type's policy, so the type
        /// is known and will be passed in the message NewGrainType header.
        /// </summary>
        public string GrainType { get; private set; }

        private PlacementResult()
        { }

        public static PlacementResult IdentifySelection(ActivationAddress address)
        {
            return
                new PlacementResult
                    {
                        Activation = address.Activation,
                        Silo = address.Silo
                    };
        }

        public static PlacementResult
            SpecifyCreation(
                SiloAddress silo,
                PlacementStrategy placement,
                string grainType)
        {
            if (silo == null)
                throw new ArgumentNullException("silo");
            if (placement == null)
                throw new ArgumentNullException("placement");
            if (string.IsNullOrWhiteSpace(grainType))
                throw new ArgumentException("'grainType' must contain a valid typename.");

            return
                new PlacementResult
                    {
                        Activation = ActivationId.NewId(),
                        Silo = silo,
                        PlacementStrategy = placement,
                        GrainType = grainType
                    };
        }

        public ActivationAddress ToAddress(GrainId grainId)
        {
            return ActivationAddress.GetAddress(Silo, grainId, Activation);
        }

        public override string ToString()
        {
            var placementStr = IsNewPlacement ? PlacementStrategy.ToString() : "*not-new*";
            return String.Format("PlacementResult({0}, {1}, {2}, {3})",
                Silo, Activation, placementStr, GrainType);
        }
    }
}
