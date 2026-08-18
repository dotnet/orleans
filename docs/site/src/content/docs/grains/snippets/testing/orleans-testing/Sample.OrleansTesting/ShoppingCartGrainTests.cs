using Moq;
using Orleans.TestKit;

namespace Tests;

// <testkit_grain_test>
public sealed class ShoppingCartGrainTests : TestKitBase
{
    [Fact]
    public async Task AddItemPersistsStateAndNotifiesAuditGrain()
    {
        var state = new ShoppingCartState();
        Silo.AddPersistentState("cart", state: state);
        var audit = Silo.AddProbe<IAuditGrain>("customer-42");
        var grain = await Silo.CreateGrainAsync<ShoppingCartGrain>("customer-42");

        await grain.AddItem("coffee");

        Assert.Equal(["coffee"], state.Items);
        Assert.Equal(1, Silo.StorageManager.GetStorageStats("cart")?.Writes);
        audit.Verify(grain => grain.RecordItemAdded("coffee"), Times.Once);
    }
}
// </testkit_grain_test>
