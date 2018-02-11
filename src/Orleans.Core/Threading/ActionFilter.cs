using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Threading
{
    internal abstract class ActionFilter<T> where T : IExecutable
    {
        public virtual void OnActionExecuting(T executable) { }

        public virtual void OnActionExecuted(T executable) { }
    }

    internal abstract class ExceptionFilter<T> where T : IExecutable
    {
        public virtual bool ExceptionHandler(Exception ex, T executable)
        {
            return false;
        }
    }

    internal sealed class ActionLambdaFilter<T> : ActionFilter<T> where T : IExecutable
    {
        private readonly Action<T> onActionExecuting;

        private readonly Action<T> onActionExecuted;

        public ActionLambdaFilter(Action<T> onActionExecuting = null, Action<T> onActionExecuted = null)
        {
            if (onActionExecuting == null && onActionExecuted == null)
            {
                throw new ArgumentNullException("Lambda filter requires at least one non-null parameter to be functional");
            }

            this.onActionExecuting = onActionExecuting ?? NoOpFilter;
            this.onActionExecuted = onActionExecuted ?? NoOpFilter;
        }

        public override void OnActionExecuting(T executable)
        {
            onActionExecuting(executable);
        }

        public override void OnActionExecuted(T executable)
        {
            onActionExecuted(executable);
        }

        private static readonly Action<T> NoOpFilter = _ => { };
    }

    internal abstract class ExecutionActionFilter : ActionFilter<ExecutionContext>
    {
    }

    internal abstract class ExecutionExceptionFilter : ExceptionFilter<ExecutionContext>
    {
    }

    internal class FiltersApplicant<T> where T : IExecutable
    {
        private readonly ActionFilter<T>[] actionFilters;

        private readonly ActionFilter<T>[] reverseOrderActionFilters;

        private readonly ExceptionFilter<T>[] exceptionFilters;

        public FiltersApplicant(
            IEnumerable<ActionFilter<T>> actionFilters, 
            IEnumerable<ExceptionFilter<T>> exceptionFilters)
        {
            this.actionFilters = actionFilters.ToArray();
            this.reverseOrderActionFilters = this.actionFilters.Reverse().ToArray();
            this.exceptionFilters = exceptionFilters.ToArray();
        }

        public void Apply(T action)
        {
            foreach (var filter in actionFilters)
            {
                filter.OnActionExecuting(action);
            }

            try
            {
                action.Execute();
            }
            catch (Exception ex)
            {
                foreach (var filter in exceptionFilters)
                {
                    if (filter.ExceptionHandler(ex, action))
                    {
                        return;
                    }
                }

                throw;
            }
            finally
            {
                foreach (var filter in reverseOrderActionFilters)
                {
                    filter.OnActionExecuted(action);
                }
            }
        }
    }
}
