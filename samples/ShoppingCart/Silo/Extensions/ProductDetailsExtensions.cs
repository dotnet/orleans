namespace Orleans.ShoppingCart.Silo.Extensions;

internal static class ProductDetailsExtensions
{
    internal static Faker<ProductDetails> GetBogusFaker(this ProductDetails productDetails) =>
        new Faker<ProductDetails>()
            .StrictMode(true)
            .RuleFor(p => p.Id, (f, p) => f.Random.Number(1, 100_000).ToString())
            .RuleFor(p => p.Name, (f, p) => f.Commerce.ProductName())
            .RuleFor(p => p.Description, (f, p) => f.Lorem.Sentence())
            .RuleFor(p => p.UnitPrice, (f, p) => decimal.Parse(f.Commerce.Price(max: 170)))
            .RuleFor(p => p.Quantity, (f, p) => f.Random.Number(0, 1_200))
            .RuleFor(p => p.ImageUrl, (f, p) => f.Image.PicsumUrl())
            .RuleFor(p => p.Category, (f, p) => f.PickRandom<ProductCategory>())
            .RuleFor(p => p.DetailsUrl, (f, p) => f.Internet.Url());
}
