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

using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans;

namespace AdventureGrainInterfaces
{
    /// <summary>
    /// A room is any location in a game, including outdoor locations and
    /// spaces that are arguably better described as moist, cold, caverns.
    /// </summary>
    public interface IRoomGrain : Orleans.IGrainWithIntegerKey
    {
        // Rooms have a textual description
        Task<string> Description(PlayerInfo whoisAsking);
        Task SetInfo(RoomInfo info);

        Task<IRoomGrain> ExitTo(string direction);

        // Players can enter or exit a room
        Task Enter(PlayerInfo player);
        Task Exit(PlayerInfo player);

        // Players can enter or exit a room
        Task Enter(MonsterInfo monster);
        Task Exit(MonsterInfo monster);

        // Things can be dropped or taken from a room
        Task Drop(Thing thing);
        Task Take(Thing thing);
        Task<Thing> FindThing(string name);

        // Players and monsters can be killed, if you have the right weapon.
        Task<PlayerInfo> FindPlayer(string name);
        Task<MonsterInfo> FindMonster(string name);
    }
}
