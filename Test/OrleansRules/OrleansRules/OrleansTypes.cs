using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.FxCop.Sdk;

namespace OrleansRules
{
    class OrleansTypes
    {
        public static ClassNode AsyncValue { get; private set; }
        public static ClassNode CompilerGeneratedAttribute { get; private set; }
        public static ClassNode NotImplementedException { get; private set; }

        static OrleansTypes()
        {
            foreach (AssemblyNode asm in RuleUtilities.AnalysisAssemblies)
            {
                var t = asm.GetType(Identifier.For("Platform.Orleans"), Identifier.For("AsyncCompletion"), true);
                if (t != null)
                {
                    var lib = t.DeclaringModule.ContainingAssembly;

                    AsyncValue = (ClassNode)lib.GetType(Identifier.For("Platform.Orleans"), Identifier.For("AsyncValue"), false);

                }
            }

            foreach (AssemblyNode asm in RuleUtilities.AnalysisAssemblies)
            {
                var t = asm.GetType(Identifier.For("System"), Identifier.For("Int32"), true);
                if (t != null)
                {
                    var lib = t.DeclaringModule.ContainingAssembly;

                    CompilerGeneratedAttribute = (ClassNode)lib.GetType(Identifier.For("System.Runtime.CompilerServices"), Identifier.For("CompilerGeneratedAttribute"), false);
                    NotImplementedException = (ClassNode)lib.GetType(Identifier.For("System"), Identifier.For("NotImplementedException"), false);
                }
            }
        }        
    }
}
