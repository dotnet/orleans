using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator;

internal class CopierGenerator(IGeneratorServices generatorServices)
{
    private const string BaseTypeCopierFieldName = "_baseTypeCopier";
    private const string ActivatorFieldName = "_activator";
    private readonly IGeneratorServices _generatorServices = generatorServices;

    private LibraryTypes LibraryTypes => _generatorServices.LibraryTypes;

    private bool HotReloadSafe => _generatorServices.Options.HotReloadSafe;

    private bool UseHotReloadShape(ISerializableTypeDescription type)
        => HotReloadSafe && !type.IsEnumType && type.SupportsHotReloadMemberAddition;

    /// <summary>
    /// Returns the copier declaration for <paramref name="type"/>, written without enclosing indentation,
    /// or <see langword="null"/> when a shared shallow copier is registered instead.
    /// </summary>
    public string? GenerateCopier(
        ISerializableTypeDescription type,
        Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers)
    {
        var plan = CreatePlan(type, defaultCopiers);
        if (plan is null)
        {
            return null;
        }

        var writer = new SourceWriter();
        Write(writer, plan);
        return writer.ToString();
    }

    internal CopierEmissionPlan? CreatePlan(
        ISerializableTypeDescription type,
        Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers,
        SerializableTypeEmissionPlan? typePlan = null)
    {
        var isShallowCopyable = type.IsShallowCopyable;
        if (isShallowCopyable && !type.IsGenericType)
        {
            defaultCopiers.Add(type, LibraryTypes.ShallowCopier.ToTypeSyntax(type.TypeSyntax));
            return null;
        }

        var members = new List<ISerializableMember>();
        foreach (var member in type.Members)
        {
            if (!member.IsCopyable)
            {
                continue;
            }

            if (member is ISerializableMember serializable)
            {
                members.Add(serializable);
            }
            else if (member is IFieldDescription or IPropertyDescription)
            {
                members.Add(new SerializableMember(_generatorServices, member));
            }
            else if (member is MethodParameterFieldDescription methodParameter)
            {
                members.Add(new SerializableMethodMember(methodParameter));
            }
        }

        var isExceptionType = type.IsExceptionType && type.SerializationHooks.Count == 0;

        var baseType = isExceptionType
            ? $"global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ExceptionCopier<{CodeText.Type(type.TypeSyntax)}, {CodeText.Type(type.BaseType.ToTypeSyntax())}>"
            : CodeText.Type((isShallowCopyable ? LibraryTypes.ShallowCopier : LibraryTypes.DeepCopier_1).ToTypeSyntax(type.TypeSyntax));

        var fields = new List<GeneratedFieldDescription>();
        var onlyDeepFields = false;
        var memberPlans = ImmutableArray<MemberEmissionPlan>.Empty;
        if (!isShallowCopyable)
        {
            fields = GetFieldDescriptions(type, members, isExceptionType, out onlyDeepFields);
            var copiers = type.IsUnsealedImmutable ? null : fields.OfType<ICopierDescription>().Concat(LibraryTypes.StaticCopiers).ToList();
            // Value-type plans omit shallow members: copying the original struct already preserves those fields.
            memberPlans = members.Where(member => !onlyDeepFields || !member.IsShallowCopyable).Select(member =>
            {
                var getter = member.GetGetter();
                var setter = member.GetSetter();
                return new MemberEmissionPlan(member.Member.FieldId, member.IsPrimaryConstructorParameter,
                    member.IsValueType, getter, setter, null, null, GenerateMemberCopy(fields, copiers, member));
            }).ToImmutableArray();
        }

        var construction = new GeneratedTypeConstruction(LibraryTypes, fields,
            !isShallowCopyable && !isExceptionType && !type.IsSealedType, isExceptionType && !isShallowCopyable);
        return new(typePlan ?? SerializableTypeEmissionPlan.Create(type, LibraryTypes), isShallowCopyable, isExceptionType,
            onlyDeepFields, baseType, CodeText.Type(LibraryTypes.BaseCopier_1.ToTypeSyntax(type.TypeSyntax)),
            ConstructionEmissionPlan.Create(construction),
            fields.Select(FieldEmissionPlan.Create).ToImmutableArray(), memberPlans);
    }

    internal static void Write(SourceWriter writer, CopierEmissionPlan plan)
    {
        var type = plan.Type;
        var simpleClassName = GetSimpleClassName(type.Name);
        var isExceptionType = plan.IsExceptionType;

        var declaration = new StringBuilder();
        declaration.Append(type.IsPublic ? "public" : "internal").Append(" sealed class ").Append(simpleClassName);
        type.AppendTypeParameterList(declaration);
        declaration.Append(" : ").Append(plan.BaseType);
        if (!plan.IsShallowCopyable && plan.Construction.HasBaseContract)
        {
            declaration.Append(", ").Append(plan.AdditionalInterface);
        }

        writer.WriteLine(GeneratedCodeUtilities.GeneratedCodeAttributeText);
        GenericParameterEmissionPlan.WriteDeclarationLine(writer, declaration, type.TypeParameters);
        writer.OpenBlock();
        if (!plan.IsShallowCopyable)
        {
            var members = plan.Members.ToList();
            WriteFieldDeclarations(writer, plan.Fields);
            if (!isExceptionType)
            {
                WriteMemberwiseDeepCopyMethod(writer, type, plan.Fields, members, plan.OnlyDeepFields);
            }

            if (isExceptionType || plan.Fields.Any(HasConstructorStatement))
            {
                WriteConstructorDeclaration(writer, simpleClassName, plan.Construction, isExceptionType ? "base(codecProvider)" : null);
                writer.OpenBlock();
                WriteConstructorStatements(writer, plan.Fields);
                writer.CloseBlock();
            }

            if (isExceptionType || plan.Construction.HasBaseContract)
            {
                WriteBaseCopierDeepCopyMethod(writer, type, members, isExceptionType);
            }
        }

        writer.CloseBlock();
    }

    internal GeneratedTypeConstruction GetConstruction(ISerializableTypeDescription type)
    {
        if (type.IsShallowCopyable)
            return new(LibraryTypes, [], hasBaseContract: false);

        var isExceptionType = type.IsExceptionType && type.SerializationHooks.Count == 0;
        return new(LibraryTypes, GetFieldDescriptions(type, [], isExceptionType, out _),
            !isExceptionType && !type.IsSealedType, isExceptionType);
    }

    public static string GetSimpleClassName(ISerializableTypeDescription serializableType) => GetSimpleClassName(serializableType.Name);

    public static string GetSimpleClassName(string name) => $"Copier_{name}";

    private List<GeneratedFieldDescription> GetFieldDescriptions(
        ISerializableTypeDescription serializableTypeDescription,
        List<ISerializableMember> members,
        bool isExceptionType,
        out bool onlyDeepFields)
    {
        var serializationHooks = serializableTypeDescription.SerializationHooks;
        onlyDeepFields = serializableTypeDescription.IsValueType && serializationHooks.Count == 0;

        var fields = new List<GeneratedFieldDescription>();

        var hotReloadShape = UseHotReloadShape(serializableTypeDescription);
        if (hotReloadShape)
        {
            fields.Add(new CodecProviderFieldDescription(LibraryTypes.ICodecProvider.ToTypeSyntax()));
        }

        if (!isExceptionType && serializableTypeDescription.HasComplexBaseType)
        {
            fields.Add(GetBaseTypeField(serializableTypeDescription));
        }

        if (!serializableTypeDescription.IsImmutable)
        {
            if (!isExceptionType && serializableTypeDescription.UseActivator && !serializableTypeDescription.IsAbstractType)
            {
                onlyDeepFields = false;
                fields.Add(new ActivatorFieldDescription(LibraryTypes.IActivator_1.ToTypeSyntax(serializableTypeDescription.TypeSyntax), ActivatorFieldName));
            }

            // Add a copier field for any field in the target which does not have a static copier.
            GetCopierFieldDescriptions(serializableTypeDescription.Members, fields, hotReloadShape);
        }

        foreach (var member in members)
        {
            if (onlyDeepFields && member.IsShallowCopyable) continue;

            AddFieldAccessorDescriptions(fields, member);
        }

        for (var hookIndex = 0; hookIndex < serializationHooks.Count; ++hookIndex)
        {
            var hookType = serializationHooks[hookIndex];
            fields.Add(new SerializationHookFieldDescription(hookType.ToTypeSyntax(), $"_hook{hookIndex}"));
        }

        return fields;
    }

    private BaseCopierFieldDescription GetBaseTypeField(ISerializableTypeDescription serializableTypeDescription)
    {
        var baseType = serializableTypeDescription.BaseType;
        if (baseType.HasAttribute(LibraryTypes.GenerateSerializerAttribute)
            && (SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, LibraryTypes.Compilation.Assembly) || baseType.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
            && baseType is not INamedTypeSymbol { IsGenericType: true })
        {
            // Use the concrete generated type and avoid expensive interface dispatch (except for generic types that will fall back to IBaseCopier<T>)
            return new(QualifiedName(ParseName(GetGeneratedNamespaceName(baseType)), IdentifierName(GetSimpleClassName(baseType.Name))), true);
        }

        return new(LibraryTypes.BaseCopier_1.ToTypeSyntax(serializableTypeDescription.BaseTypeSyntax));
    }

    public void GetCopierFieldDescriptions(IEnumerable<IMemberDescription> members, List<GeneratedFieldDescription> fields, bool lazyInitialization = false)
    {
        var copierMembers = new List<IMemberDescription>();
        var uniqueTypes = new HashSet<IMemberDescription>(MemberDescriptionTypeComparer.Default);
        foreach (var member in members)
        {
            if (!member.IsCopyable)
            {
                continue;
            }

            var t = member.Type;

            if (LibraryTypes.IsShallowCopyable(t))
                continue;

            if (LibraryTypes.StaticCopiers.Any(c => SymbolEqualityComparer.Default.Equals(c.UnderlyingType, t)))
                continue;

            if (member.Symbol.HasAttribute(LibraryTypes.ImmutableAttribute))
                continue;

            if (!uniqueTypes.Add(member))
                continue;

            copierMembers.Add(member);
        }

        var fieldNames = GeneratedFieldNames.ForTypes("_copier", copierMembers);
        for (var i = 0; i < copierMembers.Count; i++)
        {
            var member = copierMembers[i];
            fields.Add(new CopierFieldDescription(GetCopierType(member), fieldNames[i], member.Type, lazyInitialization));
        }
    }

    private TypeSyntax GetCopierType(IMemberDescription member)
    {
        var t = member.Type;
        if (t.HasAttribute(LibraryTypes.GenerateSerializerAttribute)
            && (SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, LibraryTypes.Compilation.Assembly) || t.ContainingAssembly.HasAttribute(LibraryTypes.TypeManifestProviderAttribute))
            && t is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 0 })
        {
            // Use the concrete generated type and avoid expensive interface dispatch (except for complex nested cases that will fall back to IDeepCopier<T>)
            SimpleNameSyntax name;
            if (t is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
            {
                // Construct the full generic type name
                name = GenericName(Identifier(GetSimpleClassName(t.Name)), TypeArgumentList(SeparatedList(namedTypeSymbol.TypeArguments.Select(arg => arg.ToTypeSyntax()))));
            }
            else
            {
                name = IdentifierName(GetSimpleClassName(t.Name));
            }

            return QualifiedName(ParseName(GetGeneratedNamespaceName(t)), name);
        }

        if (t is IArrayTypeSymbol { IsSZArray: true } array)
        {
            return LibraryTypes.ArrayCopier.Construct(array.ElementType).ToTypeSyntax();
        }

        if (LibraryTypes.WellKnownCopiers.FindByUnderlyingType(t) is { } copier)
        {
            // The copier is not a static copier and is also not a generic copiers.
            return copier.CopierType.ToTypeSyntax();
        }

        if (t is INamedTypeSymbol { ConstructedFrom: ISymbol unboundFieldType } named && LibraryTypes.WellKnownCopiers.FindByUnderlyingType(unboundFieldType) is { } genericCopier)
        {
            // Construct the generic copier type using the field's type arguments.
            return genericCopier.CopierType.Construct([.. named.TypeArguments]).ToTypeSyntax();
        }

        // Use the IDeepCopier<T> interface
        return LibraryTypes.DeepCopier_1.ToTypeSyntax(member.TypeSyntax);
    }

    private static void WriteMemberwiseDeepCopyMethod(
        SourceWriter writer,
        SerializableTypeEmissionPlan type,
        EquatableArray<FieldEmissionPlan> copierFields,
        List<MemberEmissionPlan> members,
        bool onlyDeepFields)
    {
        // A value type whose members are all deep-copied copies the original in place, so its parameter is the result.
        var originalParam = type.IsValueType && onlyDeepFields && !type.IsAbstractType && !type.IsUnsealedImmutable ? "result" : "original";
        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public {type.Type} DeepCopy({type.Type} {originalParam}, {type.Library.CopyContext} context)");
        writer.OpenBlock();

        var membersCopied = false;
        if (type.IsAbstractType)
        {
            writer.WriteLine("return context.DeepCopy(original);");
            membersCopied = true;
        }
        else if (type.IsUnsealedImmutable)
        {
            writer.WriteLine($"return original is null || original.GetType() == typeof({type.Type}) ? original : context.DeepCopy(original);");
            membersCopied = true;
        }
        else if (!type.IsValueType)
        {
            if (type.TrackReferences)
            {
                writer.WriteLine($"if (context.TryGetCopy(original, out {type.Type} existing))");
                WriteEmbedded(writer, "return existing;");
            }
            else
            {
                writer.WriteLine("if (original is null)");
                WriteEmbedded(writer, "return null;");
            }

            if (!type.IsSealedType)
            {
                writer.WriteLine($"if (original.GetType() != typeof({type.Type}))");
                WriteEmbedded(writer, "return context.DeepCopy(original);");
            }

            writer.WriteLine($"var result = {GetCreateValueExpression(type, copierFields)};");
            if (type.TrackReferences)
            {
                writer.WriteLine("context.RecordCopy(original, result);");
            }

            if (!type.IsSealedType)
            {
                writer.WriteLine("DeepCopy(original, result, context);");
                writer.WriteLine("return result;");
                membersCopied = true;
            }
            else if (type.HasComplexBaseType)
            {
                writer.WriteLine($"{BaseTypeCopierFieldName}.DeepCopy(original, result, context);");
            }
        }
        else if (!onlyDeepFields)
        {
            writer.WriteLine($"var result = {GetCreateValueExpression(type, copierFields)};");
        }

        if (!membersCopied)
        {
            WriteMemberwiseCopy(writer, type, members, originalParam, "context", "result");
            writer.WriteLine("return result;");
        }

        writer.CloseBlock();

        static void WriteEmbedded(SourceWriter writer, string statement)
        {
            writer.Indent();
            writer.WriteLine(statement);
            writer.Unindent();
        }
    }

    private static string GetCreateValueExpression(SerializableTypeEmissionPlan type, EquatableArray<FieldEmissionPlan> copierFields)
    {
        if (!type.UseActivator)
        {
            return type.ObjectCreation;
        }

        var activator = copierFields.FirstOrDefault(static field => field.Kind == EmissionFieldKind.Service && field.FieldName == ActivatorFieldName);
        Debug.Assert(activator is not null);
        return $"{activator!.FieldName}.Create()";
    }

    private static void WriteBaseCopierDeepCopyMethod(
        SourceWriter writer,
        SerializableTypeEmissionPlan type,
        List<MemberEmissionPlan> members,
        bool isExceptionType)
    {
        // An exception copier inherits its base copy unless the generated type adds members or callbacks.
        if (isExceptionType && members.Count == 0
            && !type.SerializationHooks.Any(static callback => callback.Method is "OnCopying" or "OnCopied"))
        {
            return;
        }

        writer.WriteLine(GeneratedCodeUtilities.MethodImplAttributeText);
        writer.WriteLine($"public {(isExceptionType ? "override " : string.Empty)}void DeepCopy({type.Type} input, {type.Type} output, {type.Library.CopyContext} context)");
        writer.OpenBlock();
        if (type.HasComplexBaseType)
        {
            writer.WriteLine($"{(isExceptionType ? "base" : BaseTypeCopierFieldName)}.DeepCopy(input, output, context);");
        }

        WriteMemberwiseCopy(writer, type, members, "input", "context", "output");
        writer.CloseBlock();
    }

    private static void WriteMemberwiseCopy(
        SourceWriter writer,
        SerializableTypeEmissionPlan type,
        List<MemberEmissionPlan> members,
        string source,
        string context,
        string destination)
    {
        WriteSerializationCallbacks(writer, type, source, destination, "OnCopying");
        foreach (var member in members.OrderBy(static m => m.FieldId))
        {
            writer.WriteLine(member.GetSetter(destination, member.GetCopy(source, context)) + ";");
        }

        WriteSerializationCallbacks(writer, type, source, destination, "OnCopied");
    }

    /// <summary>
    /// Plans the copy of a member value: shallow values are assigned, and other values are copied by a static, generated,
    /// or context-resolved copier.
    /// </summary>
    public MemberCopyPlan GenerateMemberCopy(
        List<GeneratedFieldDescription> copierFields,
        List<ICopierDescription>? copiers,
        ISerializableMember member)
    {
        if (copiers is null || member.IsShallowCopyable)
            return MemberCopyPlan.Value;

        var description = member.Member;

        // Copiers can either be static classes or injected into the constructor.
        // Either way, the member signatures are the same.
        var memberType = description.Type;
        var copier = copiers.Find(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
        if (copier is null)
        {
            return new(MemberCopyKind.Context);
        }

        string copierExpression;
        var staticCopier = LibraryTypes.StaticCopiers.FindByUnderlyingType(memberType);
        if (staticCopier != null)
        {
            copierExpression = CodeText.Type(staticCopier.CopierType.ToNameSyntax());
        }
        else
        {
            var instanceCopier = copierFields.OfType<CopierFieldDescription>().First(f => SymbolEqualityComparer.Default.Equals(f.UnderlyingType, memberType));
            copierExpression = GetCodecExpression(instanceCopier);
        }

        // If the member type differs from the copier type (eg because the member is an array), cast the result.
        var castType = SymbolEqualityComparer.Default.Equals(copier.UnderlyingType, member.Member.Type) ? null : CodeText.Type(description.TypeSyntax);
        return new(MemberCopyKind.Copier, copierExpression, castType);
    }

    private static void WriteSerializationCallbacks(SourceWriter writer, SerializableTypeEmissionPlan type, string originalInstance, string resultInstance, string callbackMethodName)
    {
        foreach (var callback in type.SerializationHooks)
        {
            if (callback.Method != callbackMethodName)
            {
                continue;
            }

            writer.WriteLine($"_hook{callback.HookIndex}.{callbackMethodName}({(callback.FirstByRef ? "ref " : string.Empty)}{originalInstance}, {(callback.SecondByRef ? "ref " : string.Empty)}{resultInstance});");
        }
    }

    internal sealed class BaseCopierFieldDescription(TypeSyntax fieldType, bool concreteType = false) : GeneratedFieldDescription(fieldType, BaseTypeCopierFieldName)
    {
        public override bool IsInjected { get; } = !concreteType;
    }

    internal sealed class CopierFieldDescription(TypeSyntax fieldType, string fieldName, ITypeSymbol underlyingType, bool lazyInitialization = false) : GeneratedFieldDescription(fieldType, fieldName), ICopierDescription
    {
        public ITypeSymbol UnderlyingType { get; } = underlyingType;
        public override bool IsInjected => false;
        public override bool LazyInitialization { get; } = lazyInitialization;
    }

}
