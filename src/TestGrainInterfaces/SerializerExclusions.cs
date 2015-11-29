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
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.UnitTest.GrainInterfaces
{
    public class SubDictionary : Dictionary<int, ulong>
    {
    }

    public class MyTypeWithAPrivateTypeField
    {
        private MyPrivateDependency _dependency;

        public MyTypeWithAPrivateTypeField()
        {
            _dependency = new MyPrivateDependency();
        }

    private class MyPrivateDependency
        {

        }
    }

    [Serializable]
    public class MyTypeWithAnInternalTypeField
    {
        private MyInternalDependency _dependency;

        public MyTypeWithAnInternalTypeField()
        {
            _dependency = new MyInternalDependency();
        }
        internal class MyInternalDependency
        {

        }
    }

    // Verify that we do not try to generate a custom serializer for MyTypeWithAPrivateTypeField.
    // If we do, compilation will fail.
    public interface IPrivateReturnType : IGrainWithIntegerKey
    {
        Task<MyTypeWithAPrivateTypeField> Foo();
    }

    // Verify that we do not try to generate a custom serializer for SubDictionary because Dictionary contains fileds of private types.
    // If we do, compilation will fail. 
    public interface ISubDictionaryReturnType : IGrainWithIntegerKey
    {
        Task<SubDictionary> Foo();
    }

    // Verify that we do generate a custom serializer for MyTypeWithAnInternalTypeField because it is visible within the assembly.
    public interface IInternalReturnType : IGrainWithIntegerKey
    {
        Task<MyTypeWithAnInternalTypeField> Foo();
    }
}
