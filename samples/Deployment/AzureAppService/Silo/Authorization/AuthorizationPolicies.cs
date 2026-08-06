// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Authorization;

internal static class AuthorizationPolicies
{
    public const string ProductManagement = nameof(ProductManagement);
    public const string ProductAdministratorRole = "ProductAdministrator";
}
