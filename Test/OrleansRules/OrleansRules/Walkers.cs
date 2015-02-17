using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.FxCop.Sdk;

namespace OrleansRules
{
    static class Walkers
    {
        /// <summary>
        /// Walk all the expressions in the supplied statement.
        /// </summary>
        public static void ForEachExpression(Statement statement, Action<Expression> expressionAction)
        {
            switch (statement.NodeType)
            {
                case NodeType.ExpressionStatement:
                    {
                        var expressionStatement = statement as ExpressionStatement;
                        ForEachExpression(expressionStatement.Expression, expressionAction);
                        break;
                    }

                case NodeType.AssignmentStatement:
                    {
                        var assignmentStatement = statement as AssignmentStatement;
                        ForEachExpression(assignmentStatement.Source, expressionAction);
                        ForEachExpression(assignmentStatement.Target, expressionAction);
                        break;
                    }

                case NodeType.Try:
                    {
                        var tryNode = statement as TryNode;

                        foreach (CatchNode catcher in tryNode.Catchers)
                        {
                            if (catcher.Filter != null)
                            {
                                ForEachExpression(catcher.Filter.Expression, expressionAction);
                            }
                        }

                        break;
                    }

                case NodeType.Branch:
                    {
                        var branch = statement as Branch;
                        ForEachExpression(branch.Condition, expressionAction);
                        break;
                    }

                case NodeType.EndFilter:
                    {
                        var endFilter = statement as EndFilter;
                        ForEachExpression(endFilter.Value, expressionAction);
                        break;
                    }

                case NodeType.SwitchInstruction:
                    {
                        var switchInstr = statement as SwitchInstruction;
                        ForEachExpression(switchInstr.Expression, expressionAction);
                        break;
                    }

                case NodeType.Return:
                    {
                        var returnNode = statement as ReturnNode;
                        ForEachExpression(returnNode.Expression, expressionAction);
                        break;
                    }

                case NodeType.Throw:
                    {
                        var throwNode = statement as ThrowNode;
                        ForEachExpression(throwNode.Expression, expressionAction);
                        break;
                    }

                case NodeType.Block:
                case NodeType.Nop:
                case NodeType.Catch:
                case NodeType.Filter:
                case NodeType.FaultHandler:
                case NodeType.Finally:
                case NodeType.EndFinally:
                case NodeType.Rethrow:
                    {
                        // nothing
                        break;
                    }

                default:
                    {
                        System.Diagnostics.Debugger.Break();
                        break;
                    }
            }
        }

        /// <summary>
        /// Walk all the expressions in the supplied method.
        /// </summary>
        public static void ForEachExpression(Method method, Action<Expression> expressionAction)
        {
            ForEachStatement(method, (statement, control) => { ForEachExpression(statement, expressionAction); });
        }

        /// <summary>
        /// Walk the expression tree and invoke the delegate for each method call encountered.
        /// </summary>
        public static void ForEachMethodCalled(Expression expr, Action<Expression, Method, NaryExpression> methodAction)
        {
            ForEachExpression(expr, (Expression embeddedExpr) => { CheckForMethodCall(expr, methodAction); });
        }

        /// <summary>
        /// Walk the statements and expressions in the method and invoke the callback for each method call encountered.
        /// </summary>
        public static void ForEachMethodCalled(Method method, Action<Expression, Method, NaryExpression> methodAction)
        {
            ForEachExpression(method, (Expression expr) => { CheckForMethodCall(expr, methodAction); });
        }

        /// <summary>
        /// Walk the expressions in the statement and invoke the callback for each method call encountered.
        /// </summary>
        public static void ForEachMethodCalled(Statement statement, Action<Expression, Method, NaryExpression> methodAction)
        {
            ForEachExpression(statement, (Expression expr) => { CheckForMethodCall(expr, methodAction); });
        }

        /// <summary>
        /// Walk the method and invoke the callback for every delegate instance created.
        /// </summary>
        public static void ForEachDelegateCreated(Method method, Action<Method, NaryExpression, TypeNode> delegateAction)
        {
            ForEachMethodCalled(method, (Expression targetObj, Method calledMethod, NaryExpression call) =>
            {
                if (calledMethod == null)
                {
                    // array construction, we don't care
                    return;
                }

                if (!(calledMethod is InstanceInitializer))
                {
                    // not a ctor call, we don't care
                    return;
                }

                if (!calledMethod.DeclaringType.IsAssignableTo(FrameworkTypes.Delegate))
                {
                    // not a ctor for a delegate type, we don't care
                    return;
                }

                if (call.Operands.Count < 2)
                {
                    // internal goo, skip
                    return;
                }

                // get the method that is being bound to the delegate

                MemberBinding memberBinding;

                if (call.Operands[1] is UnaryExpression)
                {
                    var unaryExpr = call.Operands[1] as UnaryExpression;
                    memberBinding = unaryExpr.Operand as MemberBinding;
                }
                else if (call.Operands[1] is BinaryExpression)
                {
                    var binExpr = call.Operands[1] as BinaryExpression;
                    memberBinding = binExpr.Operand2 as MemberBinding;
                }
                else
                {
                    // a function pointer assignment, can't do much with this...
                    return;
                }

                var boundMethod = memberBinding.BoundMember as Method;
                delegateAction(boundMethod, call, calledMethod.DeclaringType);
            });
        }

        /// <summary>
        /// Walk the statements and expressions in the method and invoke the callback for any field accesses encountered.
        /// </summary>
        public static void ForEachFieldAccessed(Method method, Action<Field, Expression, SourceContext> fieldAction)
        {
            // This is the fallback source context when we don't get much on the field
            // which happens when we have an assignment target.
            SourceContext fallbackSourceContext = method.SourceContext;

            ForEachExpression(method, (Expression expr) =>
            {
                if (expr.NodeType == NodeType.MemberBinding)
                {
                    var memberBinding = expr as MemberBinding;
                    var field = memberBinding.BoundMember as Field;
                    if (field != null)
                    {
                        // The target object that this field is for.
                        // Note that it is null for static fields
                        var targetObject = memberBinding.TargetObject;

                        // If we have no source context for the expression
                        // at least we can tell which method it was in and thus
                        // which source file/etc.  Much better than no information.
                        var sourceContext = expr.SourceContext;
                        if (sourceContext.FileName == null)
                        {
                            sourceContext = fallbackSourceContext;
                        }

                        fieldAction(field, targetObject, sourceContext);
                    }
                }
            });
        }

        /// <summary>
        /// Walk the expressions and invoke the callback for any parameter accesses encountered.
        /// </summary>
        public static void ForEachParameterAccessed(Expression expr, Action<Parameter, SourceContext> parameterAction)
        {
            ForEachExpression(expr, (Expression subExpr) =>
            {
                if (subExpr.NodeType == NodeType.Parameter)
                {
                    parameterAction(subExpr as Parameter, subExpr.SourceContext);
                }
            });
        }

        /// <summary>
        /// Walk the expressions and invoke the callback for any parameter accesses encountered.
        /// </summary>
        public static void ForEachParameterAccessed(Method method, Action<Parameter, SourceContext> parameterAction)
        {
            ForEachExpression(method, (Expression expr) =>
            {
                ForEachParameterAccessed(expr, parameterAction);
            });
        }

        /// <summary>
        /// Walks the statements in the method and invoke the callback for any assignment encountered.
        /// </summary>
        public static void ForEachAssignment(Method method, Action<AssignmentStatement> assignmentAction)
        {
            ForEachStatement(method, (statement, control) =>
            {
                if (statement.NodeType != NodeType.AssignmentStatement)
                {
                    // we only care about assignments
                    return;
                }

                assignmentAction(statement as AssignmentStatement);
            });
        }

        /// <summary>
        /// Walk the statements method and find any assignment to fields.
        /// </summary>
        public static void ForEachFieldWritten(Method method, Action<Field, SourceContext> fieldAction)
        {
            ForEachAssignment(method, assignmentStatement =>
            {
                var memberBinding = assignmentStatement.Target as MemberBinding;
                if (memberBinding != null)
                {
                    var field = memberBinding.BoundMember as Field;
                    if (field != null)
                    {
                        fieldAction(field, assignmentStatement.SourceContext);
                    }
                }
            });
        }

        /// <summary>
        /// Walk the statements and expressions in the method and invoke the callback for any AddressOf expressions on fields.
        /// </summary>
        public static void ForEachFieldAddressTaken(Method method, Action<Field, SourceContext> fieldAction)
        {
            ForEachExpression(method, (Expression expr) =>
            {
                if (expr.NodeType == NodeType.AddressOf)
                {
                    var unaryExpr = (UnaryExpression)expr;
                    var memberBinding = unaryExpr.Operand as MemberBinding;
                    if (memberBinding != null)
                    {
                        var field = memberBinding.BoundMember as Field;
                        if (field != null)
                        {
                            fieldAction(field, expr.SourceContext);
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Walk the expression and invoke the delegate for each leaf operand encountered.
        /// </summary>
        public static void ForEachLeafOperand(Expression expr, Action<Expression> leafAction)
        {
            ForEachExpression(expr, (Expression expr2) =>
            {
                if ((expr is UnaryExpression)
                    || (expr is BinaryExpression)
                    || (expr is TernaryExpression)
                    || (expr is NaryExpression)
                    || (expr is AddressDereference))
                {
                    return;
                }

                leafAction(expr);
            });
        }

        /// <summary>
        /// Given an expression, invoke the callback if that expression is a method call.
        /// </summary>
        static void CheckForMethodCall(Expression expr, Action<Expression, Method, NaryExpression> methodAction)
        {
            var call = expr as MethodCall;
            if (call != null)
            {
                var callee = call.Callee as MemberBinding;
                var boundMethod = callee.BoundMember as Method;

                methodAction(callee.TargetObject, boundMethod, call);
                return;
            }

            var ctor = expr as Construct;
            if (ctor != null)
            {
                var callee = ctor.Constructor as MemberBinding;
                var boundMethod = callee.BoundMember as Method;

                methodAction(callee.TargetObject, boundMethod, ctor);
                return;
            }

            var constructArray = expr as ConstructArray;
            if (constructArray != null)
            {
                methodAction(null, null, constructArray);
                return;
            }

            var indexer = expr as Indexer;
            if (indexer != null)
            {
                methodAction(indexer.Object, null, indexer);
                return;
            }
        }

        public enum Control
        {
            Normal,
            End,
        }

        /// <summary>
        /// Walk all the statements in the given method, invoking the callback for each.
        /// </summary>
        public static void ForEachStatement(Method method, Action<Statement, Control> statementAction)
        {
            if (method.Body != null)
            {
                ForEachStatement(method.Body, statementAction);
            }
        }

        /// <summary>
        /// Walk the statements in a block, invoking the callbacks as appropriate.
        /// </summary>
        static void ForEachStatement(Block block, Action<Statement, Control> statementAction)
        {
            statementAction(block, Control.Normal);

            if (block.Statements != null)
            {
                foreach (Statement statement in block.Statements)
                {
                    if (statement.NodeType == NodeType.Block)
                    {
                        ForEachStatement(statement as Block, statementAction);
                    }
                    else
                    {
                        statementAction(statement, Control.Normal);

                        if (statement.NodeType == NodeType.Try)
                        {
                            var tryNode = statement as TryNode;

                            ForEachStatement(tryNode.Block, statementAction);
                            statementAction(statement, Control.End);

                            foreach (CatchNode catchNode in tryNode.Catchers)
                            {
                                statementAction(catchNode, Control.Normal);
                                ForEachStatement(catchNode.Block, statementAction);
                                statementAction(catchNode, Control.End);
                            }

                            if (tryNode.Finally != null)
                            {
                                statementAction(tryNode.Finally, Control.Normal);
                                ForEachStatement(tryNode.Finally.Block, statementAction);
                                statementAction(tryNode.Finally, Control.End);
                            }

                            if (tryNode.FaultHandler != null)
                            {
                                statementAction(tryNode.FaultHandler, Control.Normal);
                                ForEachStatement(tryNode.FaultHandler.Block, statementAction);
                                statementAction(tryNode.FaultHandler, Control.End);
                            }
                        }
                    }
                }

                statementAction(block, Control.End);
            }
        }

        /// <summary>
        /// Walk the expression tree and invoke the delegate for each expression encountered.
        /// </summary>
        public static void ForEachExpression(Expression expr, Action<Expression> expressionAction)
        {
            if (expr == null)
            {
                return;
            }

            expressionAction(expr);

            var unaryExpr = expr as UnaryExpression;
            if (unaryExpr != null)
            {
                ForEachExpression(unaryExpr.Operand, expressionAction);
                return;
            }

            var binaryExpr = expr as BinaryExpression;
            if (binaryExpr != null)
            {
                ForEachExpression(binaryExpr.Operand1, expressionAction);
                ForEachExpression(binaryExpr.Operand2, expressionAction);
                return;
            }

            var ternaryExpr = expr as TernaryExpression;
            if (ternaryExpr != null)
            {
                ForEachExpression(ternaryExpr.Operand1, expressionAction);
                ForEachExpression(ternaryExpr.Operand2, expressionAction);
                ForEachExpression(ternaryExpr.Operand3, expressionAction);
                return;
            }

            var naryExpr = expr as NaryExpression;
            if (naryExpr != null)
            {
                foreach (Expression subExpr in naryExpr.Operands)
                {
                    ForEachExpression(subExpr, expressionAction);
                }

                var call = expr as MethodCall;
                if (call != null)
                {
                    var binding = call.Callee as MemberBinding;
                    if (binding != null)
                    {
                        if (binding != null)
                        {
                            ForEachExpression(binding.TargetObject, expressionAction);
                        }
                    }
                }

                var construct = expr as Construct;
                if (construct != null)
                {
                    ForEachExpression(construct.Constructor, expressionAction);
                    return;
                }

                var indexer = expr as Indexer;
                if (indexer != null)
                {
                    ForEachExpression(indexer.Object, expressionAction);
                    return;
                }

                return;
            }

            var addressDereference = expr as AddressDereference;
            if (addressDereference != null)
            {
                ForEachExpression(addressDereference.Address, expressionAction);
                return;
            }
        }

        /// <summary>
        /// Invoke the callback for each instances of the special lambda hack method
        /// </summary>
        public static void ForEachLambdaHackCall(Method method, ClassNode attribute, Action<Method, NaryExpression> hackAction)
        {
            Walkers.ForEachMethodCalled(method, (Expression targetObj, Method calledMethod, NaryExpression call) =>
            {

                if (calledMethod == null)
                {
                    // array init...
                    return;
                }

                if (calledMethod.DeclaringType != attribute)
                {
                    // only want calls on the desired attribute
                    return;
                }

                if (calledMethod.Name.Name != "ForLambda")
                {
                    // only want the special lamdba hack method
                    return;
                }

                hackAction(calledMethod, call);
            });
        }
    }
}
