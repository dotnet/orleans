using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Analyzers;

namespace Analyzers.Tests
{
    [TestCategory("BVT"), TestCategory("Analyzer")]
    public class NoeRefParamsDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<AlwaysInterleaveDiagnosticAnalyzer>
    {

    }
}
