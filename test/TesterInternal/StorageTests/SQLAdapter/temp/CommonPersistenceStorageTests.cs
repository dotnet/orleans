using Orleans;
using Orleans.Storage;
using System.Threading.Tasks;

namespace UnitTests.StorageTests.SQLAdapter.temp
{
    /// <summary>
    /// 
    /// </summary>
    /// <remarks>This is currently empty as it is intended to server as a discussion point.</remarks>
    public class CommonPersistenceStorageTests
    {
        private IStorageProvider Storage { get; }


        public CommonPersistenceStorageTests(IStorageProvider storage)
        {
            Storage = storage;
        }


        public async Task Store_Read()
        {
            //await Storage.ReadStateAsync();
            await TaskDone.Done;
        }


        public async Task Store_WriteRead()
        {
            //await Storage.WriteStateAsync();
            await TaskDone.Done;
        }


        public async Task Store_Delete()
        {
            //await Storage.ClearStateAsync();
            await TaskDone.Done;
        }
    }
}
