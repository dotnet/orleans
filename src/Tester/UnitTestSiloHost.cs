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
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;

namespace UnitTests.Tester
{
    /// <summary>
    /// Keep this class as a bridge to the OrleansTestingSilo package, 
    /// because it gives a convenient place to declare all the additional
    /// deployment items required by tests 
    /// - such as the TestGrain assemblies, the client and server config files.
    /// </summary>
    [DeploymentItem("OrleansConfigurationForTesting.xml")]
    [DeploymentItem("ClientConfigurationForTesting.xml")]
    [DeploymentItem("TestGrainInterfaces.dll")]
    [DeploymentItem("TestGrains.dll")]
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
            sb.AppendFormat(@"TestName={0}", context.TestName).AppendLine();
            sb.AppendFormat(@"FullyQualifiedTestClassName={0}", context.FullyQualifiedTestClassName).AppendLine();
            sb.AppendFormat(@"CurrentTestOutcome={0}", context.CurrentTestOutcome).AppendLine();
            sb.AppendFormat(@"DeploymentDirectory={0}", context.DeploymentDirectory).AppendLine();
            sb.AppendFormat(@"TestRunDirectory={0}", context.TestRunDirectory).AppendLine();
            sb.AppendFormat(@"TestResultsDirectory={0}", context.TestResultsDirectory).AppendLine();
            sb.AppendFormat(@"ResultsDirectory={0}", context.ResultsDirectory).AppendLine();
            sb.AppendFormat(@"TestRunResultsDirectory={0}", context.TestRunResultsDirectory).AppendLine();
            sb.AppendFormat(@"Properties=[ ");
            foreach (var key in context.Properties.Keys)
            {
                sb.AppendFormat(@"{0}={1} ", key, context.Properties[key]);
            }
            sb.AppendFormat(@" ]").AppendLine();
            return sb.ToString();
        }
    }
}
