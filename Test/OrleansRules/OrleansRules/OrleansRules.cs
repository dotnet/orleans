using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.FxCop.Sdk;
using OrleansRules;

namespace OrleansRules
{
    public abstract class OrleansCustomRuleBase : BaseIntrospectionRule
    {
        protected TypeNode asyncValueBaseType;

        protected OrleansCustomRuleBase(string ruleName)
            : base(ruleName,
                "OrleansRules.RuleMetadata",
                typeof(OrleansCustomRuleBase).Assembly)
        {
        }

        static Dictionary<string, List<Utils.SuppressInfo>> s_moduleSuppressions = new Dictionary<string, List<Utils.SuppressInfo>>();
        static bool s_collectedModuleInfo;
        static ModuleNode s_currentModule;
        protected static HashSet<TypeNode> s_moduleTypes = new HashSet<TypeNode>();
        protected static HashSet<TypeNode> s_discoveredTypes = new HashSet<TypeNode>();
        static object s_lock = new object();

        public virtual void CheckMethod(Method method) { }
        public virtual void CheckField(Field field) { }
        public virtual void CheckParameter(Parameter parameter) { }

        protected void CollectModuleInfo(ModuleNode module)
        {
            if (s_collectedModuleInfo)
            {
                return;
            }

            lock (s_lock)
            {
                if (s_collectedModuleInfo)
                {
                    // necessary?
                    return;
                }
                s_currentModule = module;

                GatherTypes(module.Types);

                // allows a chance to discover compiler-generated types
                if (module != null)
                {
                    Visit(module);
                }

                for (; ; )
                {
                    var snap = new TypeNode[s_discoveredTypes.Count];
                    s_discoveredTypes.CopyTo(snap);

                    VisitTypes(snap);

                    if (s_discoveredTypes.Count == snap.Length)
                    {
                        // no new compiler-generated types found
                        break;
                    }

                    s_moduleTypes.UnionWith(s_discoveredTypes);
                    s_discoveredTypes = null;
                }

                var deletables = new List<TypeNode>();
                foreach (var type in s_moduleTypes)
                {
                    if ((type.Name.Name == "<Module>")
                        || type.FullName.Contains("<PrivateImplmenetationDetails>"))
                    {
                        deletables.Add(type);
                        continue;
                    }
                    if (type.Template != null)
                    {
                        deletables.Add(type);
                        continue;
                    }
                }
                foreach (var type in deletables)
                {
                    s_moduleTypes.Remove(type);
                }

                foreach (var attr in module.Attributes)
                {
                    if (attr.Type.FullName == "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute")
                    {
                        var info = Utils.ReadSuppressInfo(attr);
                        if (!s_moduleSuppressions.ContainsKey(info.CheckId))
                        {
                            s_moduleSuppressions.Add(info.CheckId, new List<Utils.SuppressInfo>());
                        }
                        var l = s_moduleSuppressions[info.CheckId];
                        l.Add(info);
                    }
                }

                foreach (var attr in module.ContainingAssembly.ModuleAttributes)
                {
                    if (attr.Type.FullName == "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute")
                    {
                        var info = Utils.ReadSuppressInfo(attr);
                        if (!s_moduleSuppressions.ContainsKey(info.CheckId))
                        {
                            s_moduleSuppressions.Add(info.CheckId, new List<Utils.SuppressInfo>());
                        }
                        var l = s_moduleSuppressions[info.CheckId];
                        l.Add(info);
                    }
                }
            }
        }
        static void GatherTypes(TypeNodeCollection types)
        {
            foreach (var type in types)
            {
                //Console.Out.WriteLine("GatherTypes: " + type.Name.Name);
                s_moduleTypes.Add(type);

                if (type.NestedTypes != null)
                {
                    GatherTypes(type.NestedTypes);
                }
            }
        }

        void VisitTypes(TypeNode[] types)
        {
            foreach (var type in types)
            {
                if (type.ConsolidatedTemplateArguments == null || type.ConsolidatedTemplateArguments.Count == 0)
                {
                    Visit(type);
                    if ((type.NestedTypes != null) && (type.NestedTypes.Count > 0))
                    {
                        var snap = new TypeNode[type.NestedTypes.Count];
                        type.NestedTypes.CopyTo(snap, 0);
                        VisitTypes(snap);
                    }
                }
            }
        }
    }

    internal class PromiseStatus
    {
        public Node Promise { get; set; }
        public Boolean resolved { get; set; }

        public PromiseStatus(Node n, Boolean r)
        {
            Promise = n;
            resolved = r;
        }
    }

    public class EnforcePromiseWait : OrleansCustomRuleBase
    {
        private const string s_staticFieldPrefix = "s_";
        private const string s_nonStaticFieldPrefix = "m_";

        Dictionary<int, PromiseStatus> promises = new Dictionary<int, PromiseStatus>();

        private Boolean lookForAsyncValueResolve = false;

        public Boolean IsPromiseResolutionMethod(string name)
        {
            if (name == "Wait"
                || name == "ContinueWith"
                || name == "Ignore"
                || name == "GetValue")
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public sealed override TargetVisibilities TargetVisibility
        {
            get
            {
                return TargetVisibilities.All;
            }
        }

        public EnforcePromiseWait()
            : base("ConsumePromises")
        {

        }

        /*
        public override void CheckMethod(Method method)
        {
            if (method.Body == null)
            {
                return;
            }
            Console.Out.WriteLine("CheckMethod, checking: " + method.Name.Name);
            Console.Out.WriteLine("Method return type: " + method.ReturnType.Name.Name);
            // testing something first...

            for (int i = 0; i < method.Instructions.Count; i++)
            {
                var instr = method.Instructions[i];
                //Console.Out.WriteLine("opcode: " + instr.OpCode);

                if ((instr.OpCode == OpCode.Call) || (instr.OpCode == OpCode.Calli) ||
                    (instr.OpCode == OpCode.Callvirt) || (instr.OpCode == OpCode.Newobj)
                    || (instr.OpCode == OpCode.Box))
                {
                    if (i < method.Instructions.Count - 1)  // there are more instructions before the stack frame ends
                    {
                        if (method.Instructions[i + 1].OpCode == OpCode.Pop) // the return value isn't assigned to anything
                        {
                            Console.Out.WriteLine("Return value not assigned...");
                            if (instr.OpCode == OpCode.Newarr)
                            {
                                AddProblem(instr.SourceContext, "DontIgnoreReturnValueFromNewarr",
                                    method.GetFullUnmangledNameWithTypeParameters());
                            }
                            else
                            {
                                var calledMethod = instr.Value as Method;
                                var returnType = calledMethod.ReturnType;
                                //Console.Out.WriteLine("Return type: " + returnType.Name.Name);
                                if (returnType.Name.Name.StartsWith("AsyncValue"))
                                {
                                    Resolution resolution = GetResolution(calledMethod, "DontIgnoreReturnValue");
                                    Console.Out.WriteLine("Resolution: " + resolution.ToString());
                                    Problem problem = new Problem(resolution);
                                    Problems.Add(problem);
                                }
                            }
                        }
                        else
                        {
                            var calledMethod = instr.Value as Method;
                            var returnType = calledMethod.ReturnType;
                            Console.Out.WriteLine("Return type (assigned): " + returnType.Name.Name);
                        }
                    }
                }
            }

            Console.Out.WriteLine("Done analyzing method: " + method.Name.Name);
        }
         * */

        public override void VisitStatements(StatementCollection statements)
        {
            //Console.Out.WriteLine("Visit statements, visiting ------------");

            foreach (var stmt in statements)
            {
                //Console.Out.WriteLine("Statement Type: " + stmt.GetType());
                AssignmentStatement assignment = stmt as AssignmentStatement;
                if (assignment != null)
                {
                    VisitAssignmentStatement(assignment);
                }
                ExpressionStatement expression = stmt as ExpressionStatement;
                if (expression != null)
                {
                    VisitExpressionStatement(expression);
                }
                Block b = stmt as Block;
                if (b != null)
                {
                    VisitStatements(b.Statements);
                }
            }
            //Console.Out.WriteLine("VisitStatements Done visiting ------------");
            base.VisitStatements(statements);
        }

        public override void VisitAssignmentStatement(AssignmentStatement assignment)
        {
            //if (assignment.Target.Type.Name.Name.Contains("AsyncValue"))
            if (assignment.Target.Type.IsDerivedFrom(asyncValueBaseType))
            {
                lookForAsyncValueResolve = true;

                if (!promises.ContainsKey(assignment.Target.UniqueKey))
                {
                    promises.Add(assignment.Target.UniqueKey, new PromiseStatus(assignment.Target, false));
                }
            }
            base.VisitAssignmentStatement(assignment);
        }

        public MethodCall GetMethodCallFromStatement(ExpressionStatement stmt)
        {
            MethodCall call = stmt.Expression as MethodCall;
            if (call != null)
            {
                return call;
            }
            UnaryExpression unary = stmt.Expression as UnaryExpression;
            if (unary != null)
            {
                call = unary.Operand as MethodCall;
            }
            return call;
        }

        public override void VisitExpressionStatement(ExpressionStatement statement)
        {
            MethodCall call = GetMethodCallFromStatement(statement);
            if (call != null)
            {
                MemberBinding binding = call.Callee as MemberBinding;

                if (binding != null)
                {
                    if (IsPromiseResolutionMethod(binding.BoundMember.Name.Name))
                    {

                        Console.Out.WriteLine("Wait/ContinueWith/Ignore called on a promise named: "
                                + binding.BoundMember.Name.Name);
                        if (binding.TargetObject != null)
                        {
                            if (promises.ContainsKey(binding.TargetObject.UniqueKey))
                            {
                                promises[binding.TargetObject.UniqueKey].resolved = true;
                            }
                            else
                            {
                                promises.Add(binding.TargetObject.UniqueKey, new PromiseStatus(binding.TargetObject, true));
                            }
                        }
                        //Console.Out.WriteLine("Promises count: " + promises.Count);
                        MethodCall chainedCall = binding.TargetObject as MethodCall;
                        if (chainedCall != null && chainedCall.Type.IsDerivedFrom(asyncValueBaseType))
                        {
                            MemberBinding chainedBinding = chainedCall.Callee as MemberBinding;
                            Console.Out.WriteLine("Chained promise resolution found for " + chainedBinding.BoundMember.Name.Name);
                            if (promises.ContainsKey(binding.TargetObject.UniqueKey))
                            {
                                promises[binding.TargetObject.UniqueKey].resolved = true;
                            }
                            else
                            {
                                promises.Add(binding.TargetObject.UniqueKey, new PromiseStatus(binding.TargetObject, true));
                            }
                        }
                    }
                    else if (call.Type.IsDerivedFrom(asyncValueBaseType))
                    {
                        Resolution resolution = GetNamedResolution("PromiseIgnored", binding.BoundMember.Name.Name);
                        Console.Out.WriteLine("Resolution: " + resolution.ToString());
                        Problem problem = new Problem(resolution, statement);
                        if (Problems.Where((Problem p) => p.Resolution.Name.Equals(resolution.Name) && p.SourceLine == problem.SourceLine).Count() == 0)
                        {
                            Problems.Add(problem);
                        }
                    }
                    else
                    {
                        if (call.Callee != null)
                        {
                            if (call.Callee.Type != null && call.Callee.Type.IsDerivedFrom(asyncValueBaseType))
                            {
                                Resolution resolution = GetNamedResolution("PromiseIgnored", binding.BoundMember.Name.Name);
                                Console.Out.WriteLine("Resolution: " + resolution.ToString());
                                Problem problem = new Problem(resolution, statement);
                                if (Problems.Where((Problem p) => p.Resolution.Name.Equals(resolution.Name) && p.SourceLine == problem.SourceLine).Count() == 0)
                                {
                                    Problems.Add(problem);
                                }
                            }
                        }
                    }
                }
            }
            base.VisitExpressionStatement(statement);
        }

        /*public override void VisitExpression(Expression expression)
        {
            MethodCall call = expression as MethodCall;
            if (call != null)
            {
                MemberBinding binding = call.Callee as MemberBinding;
                if (binding != null)
                {
                    if (IsPromiseResolutionMethod(binding.BoundMember.Name.Name))
                    {
                        Local local = binding.TargetObject as Local;
                        if (local != null && promises.ContainsKey(local))
                        {
                            Console.Out.WriteLine("Wait/ContinueWith/Ignore called on a promise named: "
                                    + local.Name.Name);

                            promises.Remove(local);
                            //Console.Out.WriteLine("Promises count: " + promises.Count);
                        }
                        MethodCall chainedCall = binding.TargetObject as MethodCall;
                        if (chainedCall != null && chainedCall.Type.Name.Name.StartsWith("AsyncValue"))
                        {
                            MemberBinding chainedBinding = chainedCall.Callee as MemberBinding;
                            Console.Out.WriteLine("Chained promise resolution found for " + chainedBinding.BoundMember.Name.Name);
                        }
                    }
                }
                if (call.Type.Name.Name.StartsWith("AsyncValue"))
                {                    
                    Resolution resolution = GetNamedResolution("PromiseIgnored", binding.BoundMember.Name.Name);
                    Console.Out.WriteLine("Resolution: " + resolution.ToString());
                    Problem problem = new Problem(resolution, expression);
                    Problems.Add(problem);

                }
            }
        }*/

        public override void VisitReturn(ReturnNode returnInstruction)
        {
            Expression returnExpression = returnInstruction.Expression as Expression;
            if (returnExpression != null && returnExpression.Type.IsDerivedFrom(asyncValueBaseType))
            {
                Console.Out.WriteLine("async value returning from method " + returnInstruction.Expression.ToString());
                if (promises.ContainsKey(returnExpression.UniqueKey))
                {
                    promises[returnExpression.UniqueKey].resolved = true;
                }
                else
                {
                    promises.Add(returnExpression.UniqueKey, new PromiseStatus(returnExpression, true));
                }
            }
            /*
            if (lookForAsyncValueResolve)
            {
                if (promises.Count > 0)
                {
                    foreach (int p in promises.Keys)
                    {
                        if (!promises[p].resolved)
                        {
                            Resolution resolution = GetNamedResolution("PromiseUnresolved", promises[p].Promise.ToString());
                            Console.Out.WriteLine("Resolution: " + resolution.ToString());
                            Problem problem = new Problem(resolution, promises[p].Promise);
                            Problems.Add(problem);
                        }
                    }
                    promises.Clear();
                }
            }
             * */
            lookForAsyncValueResolve = false;
            base.VisitReturn(returnInstruction);
        }

        public override ProblemCollection Check(ModuleNode module)
        {
            foreach (var asmbly in module.AssemblyReferences)
            {
                var baseType = module.GetType(Identifier.For("Orleans"), Identifier.For("AsyncCompletion"), true);
                if (baseType != null)
                {
                    var type = baseType.GetNestedType(Identifier.For("AsyncValue"));

                    if (type != null)
                    {
                        asyncValueBaseType = type;
                    }
                    else
                    {
                        asyncValueBaseType = baseType;
                    }
                }
            }
            CollectModuleInfo(module);
            /*var baseAssemblyNode = AssemblyNode.GetAssembly("Orleans.dll", true, true, true);
            if (baseAssemblyNode != null)
            {
                asyncValueBaseType = baseAssemblyNode.Types.Single(t => t.Name.Name == "AsyncValue");
            }
            if (asyncValueBaseType == null)
            {
                asyncValueBaseType = s_discoveredTypes.Single(t => t.Name.Name == "AsyncValue");
            } */

            //CheckModule(module);
            foreach (var type in s_moduleTypes)
            {

                //CheckType(type);
                foreach (var member in type.Members)
                {

                    if (member.IsStatic && member.FullName.Contains("<PrivateImplementationDetails>"))
                    {
                        // magic stuff we don't care about
                        continue;
                    }

                    //Check(member);

                    var field = member as Field;
                    if (field != null)
                    {
                        CheckField(field);
                    }

                    var method = member as Method;
                    if (method != null)
                    {
                        CheckMethod(method);
                        VisitStatements(method.Body.Statements);

                        if (method.Parameters != null)
                        {
                            foreach (var param in method.Parameters)
                            {
                                CheckParameter(param);
                            }
                        }
                    }
                }
            }

            //base.Check(module);
            if (promises.Count > 0)
            {
                foreach (int p in promises.Keys)
                {
                    if (!promises[p].resolved)
                    {
                        Resolution resolution = GetNamedResolution("PromiseUnresolved", promises[p].Promise.ToString());
                        Console.Out.WriteLine("Resolution: " + resolution.ToString());
                        Problem problem = new Problem(resolution, promises[p].Promise);
                        Problems.Add(problem);
                    }
                }
            }
            return Problems;
        }
    }
}
