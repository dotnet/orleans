using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.FxCop.Sdk;
using System.Globalization;

namespace OrleansRules
{
    static class Utils
    {
#if false
        public static string Format(Variable variable, IExecutionState executionState, Method method)
        {
            return executionState.VariableName(variable, method);
        }
#endif

        public static string Format(TypeNode type, bool fullyQualified)
        {
            switch (type.NodeType)
            {
                case NodeType.Reference:
                    // CCI uses the '@' appended to the end of a reference, we need to
                    // use a '&' to make it the same as reflection
                    return Format((type as Reference).ElementType, fullyQualified) + "&";

                case NodeType.Pointer:
                    return Format((type as Pointer).ElementType, fullyQualified) + "*";

                case NodeType.OptionalModifier:
                case NodeType.RequiredModifier:
                    // CCI includes more info than Reflection, IE, in Reflection, may look like:
                    // System.UInt32
                    // in CCI, .FullName is
                    // optional(System.Security.Permissions.SecurityAction) System.UInt32
                    // we just want the last part
                    return Format((type as TypeModifier).ModifiedType, fullyQualified);

                default:
                    string typeName = type.FullName;
                    if (!fullyQualified)
                    {
                        if (type.Name == null)
                        {
                            typeName = type.FullName;
                            int lastDot = typeName.LastIndexOf('.') + 1;
                            typeName = typeName.Substring(lastDot);
                        }
                        else
                        {
                            typeName = type.Name.Name;
                        }
                    }

                    if (IsGeneric(type))
                    {
                        typeName = NormalizeGenericTypeName(typeName);
                    }

                    return typeName;
            }
        }

        public static string Format(Member member)
        {
            // For display: true
            // Local: false
            return Format(member, true, false);
        }

        public static string Format(Member member, bool forDisplay)
        {
            // Local: false
            return Format(member, forDisplay, false);
        }

        // A method that is formatted 'for display' will be 
        // used in a resolution or other place that appears
        // in the FxCop UI. The method accordingly has a short
        // type name prefix so that it can be clearly identified
        // as a stand-alone item. All parameter and return types
        // are shortened, to create a more readable string.
        //
        // A method that is not formatted for display is an
        // identifier that will be used as a key into a table
        // of type methods. The string that is returned therefore
        // needs to differentiate the method from all other 
        // possible methods on the type. Parameter and return
        // type information therefore needs to be fully qualified
        //
        // 'forDisplay == true' example:
        //
        // 'forDisplay == false' example:
        public static string Format(Member member, bool forDisplay, bool local)
        {
            var type = member as TypeNode;
            if (type != null)
            {
                // final argument indicates whether type name
                // is fully-qualified. a type item that is
                // displayed to the user should have complete
                // namespace information, so we pass forDisplay
                return Format(type, forDisplay);
            }

            string typeName = null;
            string paramsSeparator = forDisplay ? ", " : ",";
            ParameterCollection parameters = null;
            string paramsPrefix = String.Empty;
            string paramsSuffix = String.Empty;
            string returnTypeName = String.Empty;

            // false here indicates the type name should
            // not be fully-qualified
            if (!local)
            {
                typeName = Format(member.DeclaringType, false);
            }

            var m = member as Method;
            switch (member.NodeType)
            {
                case NodeType.InstanceInitializer:
                case NodeType.StaticInitializer:
                    parameters = m.Parameters;
                    paramsPrefix = "(";
                    paramsSuffix = ")";
                    break;

                case NodeType.Method:
                    parameters = m.Parameters;
                    paramsPrefix = "(";
                    paramsSuffix = "):";

                    // final argument indicates whether type should
                    // be fully qualified. if this member is not 
                    // for display, it is used as a key into a 
                    // table and therefore all type names need
                    // to be specified completely.
                    returnTypeName = Format(m.ReturnType, !forDisplay);
                    break;

                case NodeType.Property:
                    var p = member as PropertyNode;
                    parameters = p.Parameters;

                    if (parameters.Count == 0 && !forDisplay)
                    {
                        if (local)
                        {
                            return p.Name.Name;
                        }

                        return typeName + "." + p.Name.Name;
                    }

                    if (forDisplay)
                    {
                        returnTypeName = ":" + Format(p.Type, false);
                    }

                    if (parameters.Count > 0)
                    {
                        paramsPrefix = "[";
                        paramsSuffix = "]";
                    }

                    break;

                case NodeType.Field:
                    if (local)
                    {
                        return member.Name.Name;
                    }

                    return typeName + "." + member.Name.Name;

                case NodeType.Event:
                    string prefix = (local && !forDisplay) ? "e:" : String.Empty;
                    if (local)
                    {
                        return prefix + member.Name.Name;
                    }

                    return typeName + "." + member.Name.Name;

                default:
                    break;
            }

            int pieces = 4;
            if (parameters.Count > 0)
            {
                pieces = 5 + 2 * (parameters.Count - 1);
            }

            var signature = new string[pieces];

            // Example(Int32, String):Void
            // parameters.Length == 2, pieces = 7
            signature[0] = GetName(member, forDisplay); // Example......
            signature[1] = paramsPrefix; // Example(.....
            signature[pieces - 2] = paramsSuffix; // Example(...).

            // insert commas
            for (int i = 3; i < pieces - 2; i += 2)
            {
                signature[i] = paramsSeparator; // Example(.,.).
            }

            // insert parameter types: Example(Int32,String).
            for (int j = 0; j < parameters.Count; j++)
            {
                int paramIndex = 2 + j * 2;
                TypeNode paramType = parameters[j].Type;
                string paramTypeName = Format(paramType, !forDisplay);
                signature[paramIndex] = paramTypeName;
            }

            // insert return type
            signature[pieces - 1] = returnTypeName; // Example(Int32,String).Void

            if (typeName != null)
            {
                return typeName + "." + String.Concat(signature);
            }

            return String.Concat(signature);
        }

        // Formats a custom attribute in C# syntax.
        public static string Format(AttributeNode attribute)
        {
            return Format(attribute, true);
        }

        public static string Format(AttributeNode attribute, bool fullyQualified)
        {
            if (attribute == null)
            {
                throw new ArgumentNullException("attribute");
            }

            var sb = new StringBuilder();

            string typeName = Format(attribute.Type, fullyQualified);
            if (!fullyQualified && typeName.EndsWith("Attribute"))
            {
                typeName = typeName.Substring(0, typeName.Length - "Attribute".Length);
            }

            sb.Append('[');
            sb.Append(typeName);
            sb.Append('(');

            var arguments = attribute.Expressions;
            for (int i = 0, n = arguments.Count; i < n; i++)
            {
                Expression argument = arguments[i];
                var literal = argument as Literal;
                if (literal != null)
                {
                    sb.Append(LiteralToString(literal));
                }
                else
                {
                    var namedArgument = argument as NamedArgument;
                    if (namedArgument == null)
                    {
                        continue;
                    }

                    var value = ((Literal)namedArgument.Value);
                    sb.Append(namedArgument.Name.Name);
                    sb.Append(" = ");
                    sb.Append(LiteralToString(value));
                }

                if (i < n - 1)
                {
                    sb.Append(", ");
                }
            }

            sb.Append(")]");
            return sb.ToString();
        }

        public static bool IsGeneric(TypeNode typeNode)
        {
            return IsSelfGeneric(typeNode) || IsGenericArray(typeNode) || IsGenericReference(typeNode);
        }

        public static bool IsSelfGeneric(TypeNode type)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }

            if (type.IsGeneric || type is ClassParameter || type is TypeParameter)
            {
                return true;
            }

            return false;
        }

        public static bool IsGenericArray(TypeNode typeNode)
        {
            var arrayType = typeNode as ArrayType;
            if (arrayType != null)
            {
                if (IsGeneric(arrayType.ElementType))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsGenericReference(TypeNode typeNode)
        {
            var reference = typeNode as Reference;
            if (reference != null)
            {
                if (IsGeneric(reference.ElementType))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string NormalizeGenericTypeName(string value)
        {
            string noTypeParameter = value.Replace("type parameter.", string.Empty);
            var strippedNumbers = new char[noTypeParameter.Length];
            int destinationIndex = 1;

            strippedNumbers[0] = noTypeParameter[0];
            for (int i = 1; i < noTypeParameter.Length; i++)
            {
                if (noTypeParameter[i - 1] == '>' && noTypeParameter[i] != ',' && noTypeParameter[i] != '>')
                {
                    while (i < noTypeParameter.Length && noTypeParameter[i] != ',' && noTypeParameter[i] != '>')
                    {
                        i++;
                    }
                }
                else
                {
                    strippedNumbers[destinationIndex] = noTypeParameter[i];
                    destinationIndex++;
                }
            }

            return new string(strippedNumbers, 0, destinationIndex);
        }

        static string LiteralToString(Literal literal)
        {
            TypeNode type = literal.Type;
            object value = literal.Value;

            var enumNode = type as EnumNode;
            if (enumNode != null)
            {
                string enumString = EnumToString(enumNode, literal.Value);
                if (enumString != null)
                {
                    return enumString;
                }
            }

            string toString = Convert.ToString(value, CultureInfo.InvariantCulture);
            switch (type.TypeCode)
            {
                case TypeCode.String:
                    return "\"" + toString + "\"";

                case TypeCode.Boolean:
                    var b = (bool)value;
                    return b ? "true" : "false";

                default:
                    return toString;
            }
        }

        static string EnumToString(EnumNode enumNode, object value)
        {
            if (!enumNode.UnderlyingType.IsPrimitiveInteger)
            {
                return null;
            }

            ulong literalValue = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            bool flags = RuleUtilities.HasCustomAttribute(enumNode, FrameworkTypes.FlagsAttribute);

            string typeName = Format(enumNode, false);

            StringBuilder sb = null;
            var members = enumNode.Members;
            for (int i = 0, n = members.Count; i < n; i++)
            {
                var field = members[i] as Field;
                if (field == null || !field.IsLiteral)
                {
                    continue;
                }

                ulong fieldValue = Convert.ToUInt64(field.DefaultValue.Value, CultureInfo.InvariantCulture);

                if (literalValue == fieldValue)
                {
                    return typeName + "." + field.Name.Name;
                }

                if (flags && fieldValue != 0 && ((literalValue & fieldValue) == fieldValue))
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder();
                    }
                    else
                    {
                        sb.Append(" | ");
                    }

                    sb.Append(typeName);
                    sb.Append('.');
                    sb.Append(field.Name.Name);
                }
            }

            return sb == null ? null : sb.ToString();
        }

        static string GetName(Member member, bool forDisplay)
        {
            if (forDisplay)
            {
                switch (member.NodeType)
                {
                    case NodeType.InstanceInitializer:
                    case NodeType.StaticInitializer:
                        {
                            return member.DeclaringType.Name.Name;
                        }
                }
            }

            return member.Name.Name;
        }

        public static SuppressInfo ReadSuppressInfo(AttributeNode attribute)
        {
            var info = new SuppressInfo();

            var expressions = attribute.Expressions;
            var literal = (Literal)expressions[0];
            info.Category = literal.Value as string;

            literal = (Literal)expressions[1];
            info.CheckId = literal.Value as string;

            for (int i = 2, n = expressions.Count; i < n; i++)
            {
                var arg = (NamedArgument)expressions[i];
                var value = ((Literal)arg.Value).Value as string;
                switch (arg.Name.Name)
                {
                    case "Target":
                        info.Target = value;
                        break;
                    case "MessageId":
                        info.MessageId = value;
                        break;
                }
            }

            // none of the following can be null, as they end up 
            // being used for keys in to various dictionaries.
            if (info.Category == null)
            {
                info.Category = String.Empty;
            }

            if (info.CheckId == null)
            {
                info.CheckId = String.Empty;
            }
            else
            {
                var index = info.CheckId.IndexOf(':');
                if (index > 0)
                {
                    // only keep the actual id value, not the rule name
                    info.CheckId = info.CheckId.Remove(index);
                }
            }

            if (info.MessageId == null)
            {
                info.MessageId = String.Empty;
            }

            if (info.Target == null)
            {
                info.Target = String.Empty;
            }
            else
            {
                info.Target = info.Target.Replace("#", "");
                info.Target = info.Target.Replace("()", "");
            }

            return info;
        }

        public class SuppressInfo
        {
            internal string Category;
            internal string CheckId;
            internal string Target;
            internal string MessageId;

            public override string ToString()
            {
                return Category + " " + CheckId + " " + Target;
            }
        }

        static bool IsCompilerGenerated(string name)
        {
            return (name.StartsWith("CS$") // C# convention
                    || name.StartsWith("SS$") // Spec# convention
                    || name.StartsWith("VB$") // Whidbey VB convention
                    || name.StartsWith("_Vb") // Everett VB convention
                    || name.StartsWith("<")   // closures and yield state classes
                    || name.Contains("$"));   // Local variable 
        }

        public static bool IsCompilerGenerated(Local local)
        {
            return IsCompilerGenerated(local.Name.Name);
        }

        // note that this method regards all fields as 
        // compiler-emitted in the absence of pdbs
        public static bool IsCompilerGenerated(Field field)
        {
            string n = field.Name.Name;
            return (IsCompilerGenerated(n)
                    || (n.StartsWith("field") && n.Length > "field".Length));
            // this last condition indicates either a non-pdb
            // resident local, indicating its emitted by a compiler
            // or we're building without source lookups. in the 
            // latter case, we disable analysis entirely for 
            // several rules (e.g. AvoidUnusedLocals)
        }

        public static bool IsCompilerGenerated(Method method)
        {
            if (method.GetAttribute(OrleansTypes.CompilerGeneratedAttribute) != null)
            {
                return true;
            }

            // if the name contains a <, we're in business
            return method.Name.Name.Contains("<");
        }

        public static bool IsCompilerGenerated(TypeNode type)
        {
            if (type.GetAttribute(OrleansTypes.CompilerGeneratedAttribute) != null)
            {
                return true;
            }

            // if the name start with a <, we're in business
            return type.Name.Name.StartsWith("<");
        }

        /// <summary>
        /// Given an assignment statement, determine whether it performs boxing.
        /// </summary>
        internal static bool IsBoxingAssignment(AssignmentStatement assignmentStatement)
        {
            // BUGBUG: This thing needs to look at expressions, not statements!

            // first, is it a boxing node?
            if (assignmentStatement.Source.NodeType == NodeType.Box)
            {
                var expr = assignmentStatement.Source as BinaryExpression;
                var op = expr.Operand1;
                var tp = op.Type as TypeParameter;
                var cp = op.Type as ClassParameter;

                if (tp != null)
                {
                    if ((tp.TypeParameterFlags & TypeParameterFlags.ReferenceTypeConstraint) != 0)
                    {
                        // no reference type constraint, so boxing isn't always happening here so we don't report
                    }
                    else if ((tp.BaseType != null) && (!tp.BaseType.IsValueType))
                    {
                        // the type parameter is constrained by a particular reference type so no boxing
                    }
                    else
                    {
                        return true;
                    }
                }
                else if (cp != null)
                {
                    if ((cp.TypeParameterFlags & TypeParameterFlags.ReferenceTypeConstraint) != 0)
                    {
                        // no boxing
                    }
                    else if (!cp.BaseType.IsValueType)
                    {
                        // no boxing
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        public static string FindSourceFile(TypeNode type)
        {
            if (type.Members != null)
            {
                for (int count = 0; count < type.Members.Count; count++)
                {
                    Method m = type.Members[count] as Method;
                    if (m == null)
                    {
                        continue;
                    }

                    if (m.Instructions != null)
                    {
                        if (m.Instructions.Count > 1)
                        {
                            SourceContext s = m.Instructions[1].SourceContext;
                            if (s.FileName != null)
                            {
                                return s.FileName;
                            }
                        }
                    }
                }
            }

            if (type.NestedTypes != null)
            {
                foreach (var nested in type.NestedTypes)
                {
                    var src = FindSourceFile(nested);
                    if (src != null)
                    {
                        return src;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Try to find an instance of a particular attribute using canonical scoping rules.
        /// </summary>
        public static AttributeNode FindScopedAttribute(Member member, ClassNode attrType)
        {
            // look on the member itself
            var attr = member.GetAttribute(attrType);
            if (attr == null)
            {

                // look on the member's type
                if (member.DeclaringType != null)
                {
                    attr = member.DeclaringType.GetAttribute(attrType);

                    // look in all containing types
                    var type = member.DeclaringType.DeclaringType;
                    while (type != null)
                    {
                        attr = type.GetAttribute(attrType);
                        if (attr != null)
                        {
                            return attr;
                        }

                        type = type.DeclaringType;
                    }

                    // look at the type's assembly
                    attr = member.DeclaringType.DeclaringModule.ContainingAssembly.GetAttribute(attrType);
                    if (attr == null)
                    {
                        return null;
                    }
                }
                else
                {
                    var type = member as TypeNode;
                    attr = type.DeclaringModule.ContainingAssembly.GetAttribute(attrType);
                    if (attr == null)
                    {
                        return null;
                    }
                }
            }

            return attr;
        }

        /// <summary>
        /// Try to find an instance of a particular attribute using canonical scoping rules and retrieve its first argument value (which is assumed to be a boolean).
        /// </summary>
        public static bool CheckScopedBooleanAttribute(Member member, ClassNode attrType)
        {
            var attr = FindScopedAttribute(member, attrType);
            if (attr == null)
            {

                var method = member as Method;
                if (method != null)
                {
                    return CheckForLambdaHack(method, attrType);
                }

                return false;
            }

            var expr = attr.GetPositionalArgument(0);
            var lit = expr as Literal;
            if (lit != null)
            {
                if (lit.Value is bool)
                {
                    return (bool)lit.Value;
                }
            }
            else
            {
                // no argument constructor, so assume true
                return true;
            }

            return false;
        }

        /// <summary>
        /// Looks for the special lamdba hack method calls
        /// </summary>
        public static bool CheckForLambdaHack(Method method, ClassNode attribute)
        {
            bool result = false;
            Walkers.ForEachLambdaHackCall(method, attribute, (Method calledMethod, NaryExpression call) =>
            {
                result = true;
            });

            return result;
        }

        public class FieldComparer : IEqualityComparer<Field>
        {
            public static readonly FieldComparer Instance = new FieldComparer();

            public int Compare(Field x, Field y)
            {
                if (x == y)
                {
                    return 0;
                }

                if (x.Name != y.Name)
                {
                    return -1;
                }

                if (x.Type != y.Type)
                {
                    return -1;
                }

                if (x.DeclaringType != y.DeclaringType)
                {
                    if (x.DeclaringType.DeclaringModule != y.DeclaringType.DeclaringModule)
                    {
                        return -1;
                    }

                    var xn = x.DeclaringType.GetFullUnmangledNameWithTypeParameters();
                    var yn = y.DeclaringType.GetFullUnmangledNameWithTypeParameters();
                    if (xn != yn)
                    {
                        return -1;
                    }
                }

                return 0;
            }

            public bool Equals(Field x, Field y)
            {
                return Compare(x, y) == 0;
            }

            public int GetHashCode(Field obj)
            {
                return obj.Name.Name.GetHashCode();
            }
        }

        public class LocalComparer : IEqualityComparer<Local>
        {
            public static readonly LocalComparer Instance = new LocalComparer();

            public int Compare(Local x, Local y)
            {
                if (x == y)
                {
                    return 0;
                }

                if (x.Name.Name != y.Name.Name)
                {
                    return -1;
                }

                if (x.Type != y.Type)
                {
                    return -1;
                }

                return 0;
            }

            public bool Equals(Local x, Local y)
            {
                return Compare(x, y) == 0;
            }

            public int GetHashCode(Local obj)
            {
                return obj.Name.Name.GetHashCode();
            }
        }
    }

}
