﻿/*
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
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;

using Orleans;
using Orleans.Concurrency;
using OrleansXO.GrainInterfaces;

namespace OrleansXO.Grains
{
    /// <summary>
    /// Orleans grain implementation class GameGrain
    /// </summary>
    [Reentrant]
    public class PairingGrain : Grain, IPairingGrain
    {
        MemoryCache cache;

        public override Task OnActivateAsync()
        {
            cache = new MemoryCache("pairing");
            return base.OnActivateAsync();
        }

        public Task AddGame(Guid gameId, string name)
        {
            cache.Add(gameId.ToString(), name, new DateTimeOffset(DateTime.UtcNow).AddHours(1));
            return TaskDone.Done;
        }

        public Task RemoveGame(Guid gameId)
        {
            cache.Remove(gameId.ToString());
            return TaskDone.Done;
        }

        public Task<PairingSummary[]> GetGames()
        {
            return Task.FromResult(this.cache.Select(x => new PairingSummary { GameId = Guid.Parse(x.Key), Name = x.Value as string }).ToArray());
        }

    }
}
