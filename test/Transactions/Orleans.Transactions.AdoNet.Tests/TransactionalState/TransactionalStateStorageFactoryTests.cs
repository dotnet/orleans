// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.Transactions.AdoNet.Tests.Fakes;
using Orleans.Transactions.AdoNet.TransactionalState;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

/// <summary>
/// Unit tests for relational state identifier validation.
/// and related partition-key sanitization behaviour.
///
/// Because the factory's public constructor requires full Orleans service internals
/// (<c>TypeResolver</c>, <c>TypeConverter</c>, <c>GrainReferenceActivator</c>),
/// tests use <see cref="RuntimeHelpers.GetUninitializedObject"/> to create an
/// uninitialized instance and then set the <c>options</c> field via reflection
/// so that only the method under test is exercised — no DI, no silo.
/// </summary>
[TestCategory("BVT"), TestCategory("Transactions")]
public sealed class TransactionalStateStorageFactoryTests
{
    // -----------------------------------------------------------------------
    // Helper — create factory with options field set, bypassing constructor
    // -----------------------------------------------------------------------

    private static TransactionalStateStorageFactory CreateFactory(
        TransactionalStateStorageOptions opts)
    {
        // GetUninitializedObject bypasses all constructors; only the options field
        // is needed by ValidateStateId so we inject it via reflection.
        var factory = (TransactionalStateStorageFactory)
            RuntimeHelpers.GetUninitializedObject(typeof(TransactionalStateStorageFactory));

        var field = typeof(TransactionalStateStorageFactory)
            .GetField("options", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("'options' field not found on TransactionalStateStorageFactory");

        field.SetValue(factory, opts);
        return factory;
    }

    private static TransactionalStateStorageFactory CreateDefaultFactory()
        => CreateFactory(StorageTestHarness.BuildOptions());

    [Fact]
    public void CreateStateId_ComponentBoundariesDoNotCollide()
    {
        var first = TransactionalStateStorageFactory.CreateStateId("grain", "A", "B_C");
        var second = TransactionalStateStorageFactory.CreateStateId("grain", "A_B", "C");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateStateId_IsStableAndFixedLength()
    {
        var first = TransactionalStateStorageFactory.CreateStateId("grain", "service", "state");
        var second = TransactionalStateStorageFactory.CreateStateId("grain", "service", "state");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    private static string Validate(TransactionalStateStorageFactory factory, string key)
    {
        var method = typeof(TransactionalStateStorageFactory).GetMethod(
            "ValidateStateId",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("'ValidateStateId' method not found.");
        return (string)method.Invoke(factory, [key])!;
    }

    // -----------------------------------------------------------------------
    // State identifiers are parameter values and must remain lossless.
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateStateId_ForwardSlash_IsPreserved()
    {
        var factory = CreateDefaultFactory();

        var result = Validate(factory, "a/b");

        Assert.Equal("a/b", result);
    }

    [Fact]
    public void ValidateStateId_Backslash_IsPreserved()
    {
        var factory = CreateDefaultFactory();

        var result = Validate(factory, @"a\b");

        Assert.Equal(@"a\b", result);
    }

    [Fact]
    public void ValidateStateId_PoundSign_IsPreserved()
    {
        var factory = CreateDefaultFactory();

        var result = Validate(factory, "a#b");

        Assert.Equal("a#b", result);
    }

    [Fact]
    public void ValidateStateId_QuestionMark_IsPreserved()
    {
        var factory = CreateDefaultFactory();

        var result = Validate(factory, "a?b");

        Assert.Equal("a?b", result);
    }

    [Fact]
    public void ValidateStateId_DistinctKeysRemainDistinct()
    {
        var factory = CreateDefaultFactory();

        var withSeparators = Validate(factory, @"a/?#\b");
        var withUnderscores = Validate(factory, "a____b");

        Assert.NotEqual(withSeparators, withUnderscores);
    }

    [Fact]
    public void ValidateStateId_CleanKey_Unchanged()
    {
        var factory = CreateDefaultFactory();

        var result = Validate(factory, "valid_key_123");

        Assert.Equal("valid_key_123", result);
    }

    [Fact]
    public void ValidateStateId_EmptyString_ReturnsEmpty()
    {
        var factory = CreateDefaultFactory();

        var result = Validate(factory, string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ValidateStateId_SpecialCharactersRemainUnchanged()
    {
        var factory = CreateDefaultFactory();
        var input = "prefix/middle\\end#tail?suffix";

        var result = Validate(factory, input);

        Assert.Equal(input, result);
    }

    // -----------------------------------------------------------------------
    // ValidateStateId — length enforcement
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateStateId_KeyAtMaxLength_Succeeds()
    {
        var opts = StorageTestHarness.BuildOptions();
        Assert.Equal(255, opts.StateIdKeyMaxLength); // sanity-check the default

        var factory = CreateFactory(opts);
        var key255 = new string('x', 255);

        Assert.Equal(key255, Validate(factory, key255));
    }

    [Fact]
    public void ValidateStateId_KeyBelowMaxLength_Succeeds()
    {
        var opts = StorageTestHarness.BuildOptions();
        var factory = CreateFactory(opts);
        var key254 = new string('x', 254); // one below max

        var result = Validate(factory, key254);

        Assert.Equal(key254, result); // returned unchanged
    }

    [Fact]
    public void ValidateStateId_KeyLengthEqualToCustomMax_Succeeds()
    {
        var opts = StorageTestHarness.BuildOptions();
        opts.StateIdKeyMaxLength = 10;
        var factory = CreateFactory(opts);
        var key10 = new string('x', 10);

        Assert.Equal(key10, Validate(factory, key10));
    }

    [Fact]
    public void ValidateStateId_KeyOneBelowCustomMax_Succeeds()
    {
        var opts = StorageTestHarness.BuildOptions();
        opts.StateIdKeyMaxLength = 10;
        var factory = CreateFactory(opts);
        var key9 = new string('x', 9);

        var result = Validate(factory, key9);

        Assert.Equal(key9, result);
    }

    [Fact]
    public void ValidateStateId_ErrorMessageContainsKeyLength()
    {
        var opts = StorageTestHarness.BuildOptions();
        var factory = CreateFactory(opts);
        var key300 = new string('x', 300);

        var ex = Assert.Throws<TargetInvocationException>(() => Validate(factory, key300));
        var argumentException = Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains("300", argumentException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStateId_ErrorMessageContainsKeyValue()
    {
        var opts = StorageTestHarness.BuildOptions();
        var factory = CreateFactory(opts);
        var key = new string('a', 256);

        var ex = Assert.Throws<TargetInvocationException>(() => Validate(factory, key));
        var argumentException = Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains(key, argumentException.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Special characters do not affect length validation.
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateStateId_SpecialCharsAndTooLong_Throws()
    {
        var opts = StorageTestHarness.BuildOptions();
        var factory = CreateFactory(opts);
        var key = "/" + new string('x', 255); // length = 256

        var exception = Assert.Throws<TargetInvocationException>(() => Validate(factory, key));
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void ValidateStateId_SpecialCharsOnly_ArePreserved()
    {
        var factory = CreateDefaultFactory();
        const string input = "///";

        var result = Validate(factory, input);

        Assert.Equal(input, result);
    }
}
