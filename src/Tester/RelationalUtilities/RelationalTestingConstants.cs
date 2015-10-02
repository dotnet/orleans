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

namespace UnitTests.General
{
    /// <summary>
    /// Constants used by relational tests.
    /// </summary>
    public class RelationalTestingConstants
    {
        /// <summary>
        /// A key for a query template to create a database with a given name.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string CreateDatabaseKey = "CreateDatabaseTemplate";

        /// <summary>
        /// A key for a query template to drop a database with a given name.
        /// </summary>
        /// <remarks>Has vendor specific elements.</remarks>
        public const string DropDatabaseKey = "DropDatabaseTemplate";

        /// <summary>
        /// A key for a query template to delete all data in tables.
        /// </summary>
        /// <remarks>Truncating would be more efficient, but is more vendor specific due to constraints.</remarks>
        public const string DeleteAllDataKey = "DeleteAllDataTemplate";

        /// <summary>
        /// A key for a query template if a database with a given name exists.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string ExistsDatabaseKey = "ExistsDatabaseTemplate";

        /// <summary>
        /// A key to add a default connection string if none otherwise defined.
        /// This is something a given database will be listening to. If not altered during
        /// setup, usually databases have a well known default settings.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string DefaultConnectionStringKey = "DefaultConnectionString";
    }
}
