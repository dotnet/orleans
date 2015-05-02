/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.CodeDom;

using Orleans.Serialization;

namespace Orleans.CodeGeneration.Serialization
{
    class SerializerGenerationUtilities
    {
        internal static CodeMemberMethod GenerateCopier(string name, string typeName, CodeTypeParameterCollection genericTypeParams = null)
        {
            var copier = new CodeMemberMethod { Name = name };
            copier.Attributes = (copier.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            copier.Attributes = (copier.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            copier.CustomAttributes.Add(new CodeAttributeDeclaration(new CodeTypeReference(typeof(CopierMethodAttribute), CodeTypeReferenceOptions.GlobalReference)));
            copier.ReturnType = new CodeTypeReference(typeof(object));
            copier.Parameters.Add(new CodeParameterDeclarationExpression(typeof(object), "original"));
            copier.Statements.Add(new CodeVariableDeclarationStatement(typeName, "input", new CodeCastExpression(typeName, new CodeArgumentReferenceExpression("original"))));
            return copier;
        }

        internal static CodeMemberMethod GenerateSerializer(string name, string typeName, CodeTypeParameterCollection genericTypeParams = null)
        {
            var serializer = new CodeMemberMethod { Name = name };
            serializer.Attributes = (serializer.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            serializer.Attributes = (serializer.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            serializer.CustomAttributes.Add(new CodeAttributeDeclaration(new CodeTypeReference(typeof(SerializerMethodAttribute), CodeTypeReferenceOptions.GlobalReference)));
            serializer.ReturnType = new CodeTypeReference(typeof(void));
            serializer.Parameters.Add(new CodeParameterDeclarationExpression(typeof(object), "original"));
            serializer.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(BinaryTokenStreamWriter), CodeTypeReferenceOptions.GlobalReference), "stream"));
            serializer.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Type), "expected"));
            serializer.Statements.Add(new CodeVariableDeclarationStatement(typeName, "input", new CodeCastExpression(typeName, new CodeArgumentReferenceExpression("original"))));
            return serializer;
        }

        internal static CodeMemberMethod GenerateDeserializer(string name, string typeName, CodeTypeParameterCollection genericTypeParams = null)
        {
            var deserializer = new CodeMemberMethod { Name = name };
            deserializer.Attributes = (deserializer.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Public;
            deserializer.Attributes = (deserializer.Attributes & ~MemberAttributes.ScopeMask) | MemberAttributes.Static;
            deserializer.CustomAttributes.Add(new CodeAttributeDeclaration(new CodeTypeReference(typeof(DeserializerMethodAttribute), CodeTypeReferenceOptions.GlobalReference)));
            deserializer.ReturnType = new CodeTypeReference(typeof(object));
            deserializer.Parameters.Add(new CodeParameterDeclarationExpression(typeof(Type), "expected"));
            deserializer.Parameters.Add(new CodeParameterDeclarationExpression(new CodeTypeReference(typeof(BinaryTokenStreamReader), CodeTypeReferenceOptions.GlobalReference), "stream"));
            return deserializer;
        }
    }
}
