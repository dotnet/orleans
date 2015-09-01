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

using System.Text;
using NUnit.Framework;
using Orleans.TestingHost;

namespace UnitTests.Tester
{
    /// <summary>
    /// Keep this class as a bridge to the OrleansTestingSilo package, 
    /// because it gives a convenient place to declare all the additional
    /// deployment items required by tests 
    /// - such as the TestGrain assemblies, the client and server config files.
    /// </summary>
    //[DeploymentItem("OrleansConfigurationForTesting.xml")]
    //[DeploymentItem("ClientConfigurationForTesting.xml")]
    //[DeploymentItem("TestGrainInterfaces.dll")]
    //[DeploymentItem("TestGrains.dll")]
    //
    // TODO: NUnit does not use / require [DeploymentItem] tags, 
    //       so we can probably remove this class after completing the switch over to NUnit.
    //
    public class UnitTestSiloHost : TestingSiloHost
    {
        public UnitTestSiloHost() // : base()
        {
        }
        public UnitTestSiloHost(TestingSiloOptions siloOptions)
            : base(siloOptions)
        {
        }
        public UnitTestSiloHost(TestingSiloOptions siloOptions, TestingClientOptions clientOptions)
            : base(siloOptions, clientOptions)
        {
        }

        protected static string DumpTestContext(TestContext context)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(@"Test Name={0}", context.Test.Name).AppendLine();
            sb.AppendFormat(@"Test FullyQualifiedClassName={0}", context.Test.FullName).AppendLine();
            sb.AppendFormat(@"Test Outcome={0}", context.Result.Status).AppendLine();
            sb.AppendFormat(@"Test Directory={0}", context.TestDirectory).AppendLine();
            sb.AppendFormat(@"Test Working Directory={0}", context.WorkDirectory).AppendLine();
            sb.AppendFormat(@"Test Properties=[ ");
            foreach (var key in context.Test.Properties.Keys)
            {
                sb.AppendFormat(@"{0}={1} ", key, context.Test.Properties[key]);
            }
            sb.AppendFormat(@" ]").AppendLine();
            return sb.ToString();
        }
    }
}
