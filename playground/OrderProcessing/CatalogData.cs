namespace OrderProcessing;

// Initial catalog, adapted from dotnet/eShop's src/Catalog.API/Setup/catalog.json.
// Stock levels are small so that stock rejection is easy to observe.
public static class CatalogData
{
    public static readonly CatalogItem[] Items =
    [
        Item(1, "Footwear", "Daybird", "Wanderer Black Hiking Boots", 109.99m, 10),
        Item(2, "Climbing", "Gravitator", "Summit Pro Harness", 89.99m, 8),
        Item(3, "Ski/boarding", "WildRunner", "Alpine Fusion Goggles", 79.99m, 12),
        Item(4, "Bags", "Quester", "Expedition Backpack", 129.99m, 6),
        Item(5, "Ski/boarding", "B&R", "Blizzard Rider Snowboard", 299.99m, 3),
        Item(6, "Trekking", "Raptor Elite", "Carbon Fiber Trekking Poles", 69.99m, 15),
        Item(7, "Bags", "Solstix", "Explorer 45L Backpack", 149.99m, 5),
        Item(8, "Jackets", "Grolltex", "Frostbite Insulated Jacket", 179.99m, 7),
        Item(9, "Navigation", "AirStrider", "VenturePro GPS Watch", 199.99m, 4),
        Item(10, "Cycling", "Green Equipment", "Trailblazer Bike Helmet", 59.99m, 20),
        Item(11, "Climbing", "WildRunner", "Vertical Journey Climbing Shoes", 129.99m, 9),
        Item(12, "Ski/boarding", "Zephyr", "Powder Pro Snowboard", 399.99m, 2),
    ];

    public static CatalogItem? Find(int id) => (uint)(id - 1) < (uint)Items.Length ? Items[id - 1] : null;

    private static CatalogItem Item(int id, string type, string brand, string name, decimal price, int stock) =>
        new(id, type, brand, name, price, stock, ReservedStock: 0, SoldStock: 0);
}
