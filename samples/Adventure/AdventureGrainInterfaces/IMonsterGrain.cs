namespace AdventureGrainInterfaces;

public interface IMonsterGrain : IGrainWithIntegerKey
{
    // Even monsters have a name
    Task<string?> Name();
    Task SetInfo(MonsterInfo info);

    // Monsters are located in exactly one room
    Task SetRoomGrain(IRoomGrain room);
    Task<IRoomGrain> RoomGrain();

    Task<string> Kill(IRoomGrain room);
}
