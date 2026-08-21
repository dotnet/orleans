// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Orleans.Transactions.AdoNet.Storage;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class RelationalStorageTests
{
    [Fact]
    public async Task ExecuteTransactionAsync_NullOperations_ThrowsBeforeOpeningConnection()
    {
        var storage = (RelationalStorage)RuntimeHelpers.GetUninitializedObject(typeof(RelationalStorage));

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.ExecuteTransactionAsync(null!));

        Assert.Equal("multipleQuery", exception.ParamName);
    }
}
