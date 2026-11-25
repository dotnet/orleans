using System.Collections.Immutable;
using System.Text;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal class ActivatorGenerator(IGeneratorServices generatorServices)
{
    private readonly IGeneratorServices _generatorServices = generatorServices;

    /// <summary>Returns the activator declaration for <paramref name="type"/>, written without enclosing indentation.</summary>
    public string GenerateActivator(ISerializableTypeDescription type)
    {
        var writer = new SourceWriter();
        Write(writer, CreatePlan(type));
        return writer.ToString();
    }

    internal ActivatorEmissionPlan CreatePlan(ISerializableTypeDescription type, SerializableTypeEmissionPlan? typePlan = null)
        => new(typePlan ?? SerializableTypeEmissionPlan.Create(type, _generatorServices.LibraryTypes),
            CodeText.Type(_generatorServices.LibraryTypes.IActivator_1.ToTypeSyntax(type.TypeSyntax)),
            type.ActivatorConstructorParameters.Select(static parameter => CodeText.Type(parameter)).ToImmutableArray());

    internal static void Write(SourceWriter writer, ActivatorEmissionPlan plan)
    {
        var type = plan.Type;
        var simpleClassName = GetSimpleClassName(type.Name);
        var parameters = plan.ConstructorParameters;

        var declaration = new StringBuilder();
        declaration.Append("internal sealed class ").Append(simpleClassName);
        type.AppendTypeParameterList(declaration);
        declaration.Append(" : ").Append(plan.BaseInterface);
        writer.WriteLine(GeneratedCodeUtilities.GeneratedCodeAttributeText);
        GenericParameterEmissionPlan.WriteDeclarationLine(writer, declaration, type.TypeParameters);
        writer.OpenBlock();
        for (var index = 0; index < parameters.Count; index++)
        {
            writer.WriteLine($"private readonly {parameters[index]} _arg{index};");
        }

        string createObject;
        if (parameters.Count > 0)
        {
            var constructor = new StringBuilder();
            var arguments = new StringBuilder();
            constructor.Append("public ").Append(simpleClassName).Append('(');
            for (var index = 0; index < parameters.Count; index++)
            {
                if (index > 0)
                {
                    constructor.Append(", ");
                    arguments.Append(", ");
                }

                constructor.Append(parameters[index]).Append(" arg").Append(index);
                arguments.Append("_arg").Append(index);
            }

            writer.WriteLine(constructor.Append(')').ToString());
            writer.OpenBlock();
            for (var index = 0; index < parameters.Count; index++)
            {
                writer.WriteLine($"_arg{index} = OrleansGeneratedCodeHelper.UnwrapService(this, arg{index});");
            }

            writer.CloseBlock();
            createObject = $"new {type.Type}({arguments})";
        }
        else
        {
            createObject = type.ObjectCreation;
        }

        writer.WriteLine($"public {type.Type} Create() => {createObject};");
        writer.CloseBlock();
    }

    public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

    public static string GetSimpleClassName(string name) => $"Activator_{name}";

    /// <summary>
    /// Determines whether an activator should be generated for the specified type.
    /// </summary>
    internal static bool ShouldGenerateActivator(ISerializableTypeDescription type)
    {
        return !type.IsAbstractType
            && !type.IsEnumType
            && (!type.IsValueType
                && type.IsEmptyConstructable
                && !type.UseActivator
                && type is not GeneratedInvokableDescription
                || type.HasActivatorConstructor);
    }
}
