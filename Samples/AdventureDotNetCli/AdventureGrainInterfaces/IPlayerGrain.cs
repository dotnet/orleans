using Orleans;
using System.Threading.Tasks;

namespace AdventureGrainInterfaces
{
    /// <summary>
    /// A player is, well, there's really no other good name...
    /// </summary>
    public interface IPlayerGrain : Orleans.IGrainWithGuidKey
    {
        // Players have names
        Task<string> Name();
        Task SetName(string name);

        // Each player is located in exactly one room
        Task SetRoomGrain(IRoomGrain room);
        Task<IRoomGrain> RoomGrain();

        // Until Death comes knocking                
        Task Die(PlayerInfo killer, Thing weapon);

        // Send Text Messages to Player
        Task SendMessage(string message);

        // A Player takes his turn by calling Play with a command
        Task<string> Play(string command);
        Task Subscribe(IMessage obj);
        Task UnSubscribe(IMessage obj);
    }
}
