namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a type parameter in a serializable or proxy type.
/// </summary>
internal readonly record struct TypeParameterModel(string Name, string OriginalName, int Ordinal);
