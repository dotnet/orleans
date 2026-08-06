// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Extensions;

internal static class ProductDetailsExtensions
{
    internal static readonly Faker<ProductDetails> ProductDetailsFaker =
        new Faker<ProductDetails>()
            .StrictMode(true)
            .RuleFor(p => p.Id, (f, p) => f.IndexGlobal.ToString())
            .RuleFor(p => p.Name, (f, p) => f.Commerce.ProductName())
            .RuleFor(p => p.Description, (f, p) => f.Lorem.Sentence())
            .RuleFor(p => p.UnitPrice, (f, p) => decimal.Parse(f.Commerce.Price(max: 170)))
            .RuleFor(p => p.Quantity, (f, p) => f.Random.Number(0, 1_200))
            .RuleFor(p => p.ImageUrl, (f, p) => f.Image.PicsumUrl())
            .RuleFor(p => p.Category, (f, p) => f.PickRandom<ProductCategory>())
            .RuleFor(p => p.DetailsUrl, (f, p) => f.Internet.Url());

    internal static bool MatchesFilter(this ProductDetails product, string? filter)
    {
        if (filter is null or { Length: 0 })
        {
            return true;
        }

        if (product is not null)
        {
            return (product.Name is { } name && name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                || (product.Description is { } description && description.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }
}
