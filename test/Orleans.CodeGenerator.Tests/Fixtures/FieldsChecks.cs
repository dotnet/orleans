using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke;

namespace Orleans.Serialization.NativeAotFieldAccessSmoke;

internal static class FieldAccessChecks
{
    public static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection().AddSerializer();
        // Root the closed codecs and copiers so this smoke isolates generated member access.
        services.AddSingleton<IFieldCodec<PrivateFields>, Codec_PrivateFields>();
        services.AddSingleton<IDeepCopier<PrivateFields>, Copier_PrivateFields>();
        services.AddSingleton<IFieldCodec<ConstrainedFields<string>>, Codec_ConstrainedFields<string>>();
        services.AddSingleton<IDeepCopier<ConstrainedFields<string>>, Copier_ConstrainedFields<string>>();
        services.AddSingleton<IFieldCodec<ValueFields<int>>, Codec_ValueFields<int>>();
        services.AddSingleton<IDeepCopier<ValueFields<int>>, Copier_ValueFields<int>>();
        services.AddSingleton<IFieldCodec<Outer<string>.Nested<int>>, OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke.Outer.Codec_Nested<string, int>>();
        services.AddSingleton<IDeepCopier<Outer<string>.Nested<int>>, OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke.Outer.Copier_Nested<string, int>>();
        services.AddSingleton<IFieldCodec<VolatileFields>, Codec_VolatileFields>();
        services.AddSingleton<IDeepCopier<VolatileFields>, Copier_VolatileFields>();
        services.AddSingleton<IFieldCodec<VolatileValueFields>, Codec_VolatileValueFields>();
        services.AddSingleton<IDeepCopier<VolatileValueFields>, Copier_VolatileValueFields>();
        services.AddSingleton<IFieldCodec<GenericVolatileFields<string>>, Codec_GenericVolatileFields<string>>();
        services.AddSingleton<IDeepCopier<GenericVolatileFields<string>>, Copier_GenericVolatileFields<string>>();
        return services.BuildServiceProvider();
    }

    public static void PrivateAndBackingFieldsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var input = new PrivateFields(137, [2, 4, 8]);
        var serializer = services.GetRequiredService<Serializer<PrivateFields>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input))
            ?? throw new InvalidOperationException("Deserialization must return the private-field payload.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        ValidatePrivateFields(input, result, copy);
    }

    public static void ValidatePrivateFields(PrivateFields input, PrivateFields result, PrivateFields copy)
    {
        Validate(result);
        Validate(copy);
        Ensure(!ReferenceEquals(input, copy), "The class copier must create a new instance.");
        Ensure(!ReferenceEquals(input.Bytes, copy.Bytes), "The readonly array field must be deeply copied.");
        copy.Bytes[0] = 99;
        Ensure(input.Bytes[0] == 2, "Mutating the copied array must preserve the original.");

        static void Validate(PrivateFields value)
        {
            Ensure(value.Number == 137, "The private field must be restored.");
            Ensure(value.Bytes.AsSpan().SequenceEqual(new byte[] { 2, 4, 8 }), "The readonly field must be restored.");
            Ensure(value.ReadOnly == "readonly", "The get-only backing field must be restored.");
            Ensure(value.InitOnly == "init-only", "The init-only backing field must be restored.");
            Ensure(value.PrivateSetter == "private setter", "The private-setter backing field must be restored.");
            Ensure(value.PrivateValue == "private property", "The private property's backing field must be restored.");
        }
    }

    public static void ConstrainedGenericFieldsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var input = new ConstrainedFields<string>("generic fields");
        var serializer = services.GetRequiredService<Serializer<ConstrainedFields<string>>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input))
            ?? throw new InvalidOperationException("Deserialization must return the constrained generic payload.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        ValidateConstrainedFields(input, result, copy);
    }

    public static void ValidateConstrainedFields(ConstrainedFields<string> input, ConstrainedFields<string> result, ConstrainedFields<string> copy)
    {
        Ensure(result.Value == input.Value && result.ReadOnly == input.Value && result.InitOnly == input.Value,
            "The constrained generic serializer must restore private and backing fields.");
        Ensure(copy.Value == input.Value && copy.ReadOnly == input.Value && copy.InitOnly == input.Value,
            "The constrained generic copier must restore private and backing fields.");
        Ensure(!ReferenceEquals(input, copy), "The generic class copier must create a new instance.");
    }

    public static void GenericStructFieldsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var input = new ValueFields<int>(211, [3, 6, 9]);
        var serializer = services.GetRequiredService<Serializer<ValueFields<int>>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input));
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        ValidateValueFields(input, result, copy);
    }

    public static void ValidateValueFields(ValueFields<int> input, ValueFields<int> result, ValueFields<int> copy)
    {
        Ensure(result.Value == 211 && result.ReadOnly == 211 && result.Bytes.AsSpan().SequenceEqual(input.Bytes),
            "The generic struct serializer must restore readonly fields through a ref receiver.");
        Ensure(copy.Value == 211 && copy.ReadOnly == 211 && copy.Bytes.AsSpan().SequenceEqual(input.Bytes),
            "The generic struct copier must restore readonly fields through a ref receiver.");
        Ensure(!ReferenceEquals(input.Bytes, copy.Bytes), "The struct copier must deeply copy the array field.");
    }

    public static void NestedGenericFieldsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var input = new Outer<string>.Nested<int>("nested fields", 307);
        var serializer = services.GetRequiredService<Serializer<Outer<string>.Nested<int>>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input))
            ?? throw new InvalidOperationException("Deserialization must return the nested generic payload.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        ValidateNestedFields(input, result, copy);
    }

    public static void ValidateNestedFields(Outer<string>.Nested<int> input, Outer<string>.Nested<int> result, Outer<string>.Nested<int> copy)
    {
        Ensure(result.Value == input.Value && result.Number == input.Number,
            "The nested generic serializer must preserve both declaring-type parameters.");
        Ensure(copy.Value == input.Value && copy.Number == input.Number,
            "The nested generic copier must preserve both declaring-type parameters.");
        Ensure(!ReferenceEquals(input, copy), "The nested generic copier must create a new instance.");
    }

    public static void VolatileFieldsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var input = new VolatileFields(419, "volatile", [5, 10, 15], 23);
        var serializer = services.GetRequiredService<Serializer<VolatileFields>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input))
            ?? throw new InvalidOperationException("Deserialization must return the volatile-field payload.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        ValidateVolatileFields(input, result, copy);
    }

    public static void ValidateVolatileFields(VolatileFields input, VolatileFields result, VolatileFields copy)
    {
        Ensure(result.Number == input.Number && result.Text == input.Text && result.Ordinary == input.Ordinary
            && result.Bytes.AsSpan().SequenceEqual(input.Bytes), "The serializer must restore volatile and ordinary fields.");
        Ensure(copy.Number == input.Number && copy.Text == input.Text && copy.Ordinary == input.Ordinary
            && copy.Bytes.AsSpan().SequenceEqual(input.Bytes), "The copier must restore volatile and ordinary fields.");
        Ensure(!ReferenceEquals(input, copy), "The volatile-field copier must create a new instance.");
        Ensure(!ReferenceEquals(input.Bytes, copy.Bytes), "The volatile array field must be deeply copied.");
        copy.Bytes[0] = 99;
        Ensure(input.Bytes[0] == 5, "Mutating the copied volatile array must preserve the original.");
    }

    public static void VolatileValueFieldsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var input = new VolatileValueFields(421, [6, 12, 18]);
        var serializer = services.GetRequiredService<Serializer<VolatileValueFields>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input));
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        ValidateVolatileValueFields(input, result, copy);
    }

    public static void ValidateVolatileValueFields(VolatileValueFields input, VolatileValueFields result, VolatileValueFields copy)
    {
        Ensure(result.Number == input.Number && result.Bytes.AsSpan().SequenceEqual(input.Bytes),
            "The struct serializer must restore volatile fields through a ref receiver.");
        Ensure(copy.Number == input.Number && copy.Bytes.AsSpan().SequenceEqual(input.Bytes),
            "The struct copier must restore volatile fields through a ref receiver.");
        Ensure(!ReferenceEquals(input.Bytes, copy.Bytes), "The volatile struct array field must be deeply copied.");
    }

    public static void GenericVolatileFieldsRoundTripAndCopy()
    {
        using var services = CreateServices();
        var input = new GenericVolatileFields<string>("generic volatile");
        var serializer = services.GetRequiredService<Serializer<GenericVolatileFields<string>>>();
        var result = serializer.Deserialize(serializer.SerializeToArray(input))
            ?? throw new InvalidOperationException("Deserialization must return the generic volatile payload.");
        var copy = services.GetRequiredService<DeepCopier>().Copy(input);
        ValidateGenericVolatileFields(input, result, copy);
    }

    public static void ValidateGenericVolatileFields(GenericVolatileFields<string> input, GenericVolatileFields<string> result, GenericVolatileFields<string> copy)
    {
        Ensure(result.Value == input.Value, "The generic serializer must restore the volatile field.");
        Ensure(copy.Value == input.Value, "The generic copier must restore the volatile field.");
        Ensure(!ReferenceEquals(input, copy), "The generic volatile copier must create a new instance.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
