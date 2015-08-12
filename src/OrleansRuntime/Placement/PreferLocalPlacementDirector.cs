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

using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// PreferLocalPlacementDirector is a single activation placement.
    /// It is similar to RandomPlacementDirector except for how new activations are placed.
    /// When activation is requested (OnSelectActivation), it uses the same algorithm as RandomPlacementDirector to pick one if one already exists.
    /// That is, it checks with the Distributed Directory.
    /// If none exits, it prefers to place a new one in the local silo. If there are no races (only one silo at a time tries to activate this grain),
    /// the the local silo wins. In the case of concurrent activations of the first activation of this grain, only one silo wins.
    /// </summary>
    internal class PreferLocalPlacementDirector : RandomPlacementDirector
    {
        internal override Task<PlacementResult> 
            OnAddActivation(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            var grainType = context.GetGrainTypeName(grain);
            return Task.FromResult( 
                PlacementResult.SpecifyCreation(context.LocalSilo, strategy, grainType));
        }
    }
}
