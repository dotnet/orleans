using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Threading
{
    internal class ActionFilter<T> where T : IExecutable
    {
        public virtual void OnActionExecuting(T executable) { }

        public virtual void OnActionExecuted(T executable) { }

        public virtual bool ExceptionHandler(Exception ex, T executable)
        {
            return false;
        }
    }

    internal sealed class ActionLambdaFilter<T> : ActionFilter<T> where T : IExecutable
    {
        private readonly Action<T> onActionExecuting;

        private readonly Action<T> onActionExecuted;

        private readonly Func<Exception, T, bool> exceptionHandler;

        public ActionLambdaFilter(
            Action<T> onActionExecuting = null,
            Action<T> onActionExecuted = null,
            Func<Exception, T, bool> exceptionHandler = null)
        {
            if (onActionExecuting == null && onActionExecuted == null && exceptionHandler == null)
            {
                throw new ArgumentNullException("Lambda filter requires at least one non-null parameter to be functional");
            }

            this.onActionExecuting = onActionExecuting ?? NoOpFilter;
            this.onActionExecuted = onActionExecuted ?? NoOpFilter;
            this.exceptionHandler = exceptionHandler ?? NoOpHandler;
        }

        public override void OnActionExecuting(T executable)
        {
            onActionExecuting(executable);
        }

        public override void OnActionExecuted(T executable)
        {
            onActionExecuted(executable);
        }

        public override bool ExceptionHandler(Exception ex, T executable)
        {
            return exceptionHandler(ex, executable);
        }

        private static readonly Action<T> NoOpFilter = _ => { };

        private static readonly Func<Exception, T, bool> NoOpHandler = (e, c) => false;
    }

    internal class ExecutionFilter : ActionFilter<ExecutionContext>
    {
    }
    
    internal class ActionFiltersApplicant<T> where T : IExecutable
    {
        private readonly ActionFilter<T>[] filters;

        public ActionFiltersApplicant(IEnumerable<ActionFilter<T>> filters)
        {
            if (filters == null) throw new ArgumentNullException(nameof(filters));
            this.filters = filters.ToArray();
        }

        public void Apply(T action)
        {
            Apply(action, 0);
        }

        private void Apply(T action, int filterIndex)
        {
            if (filterIndex >= filters.Length)
            {
                action.Execute();
                return;
            }

            var filter = filters[filterIndex];
            try
            {
                filter.OnActionExecuting(action);
                Apply(action, filterIndex + 1);
            }
            catch (Exception ex)
            {
                if (!filter.ExceptionHandler(ex, action))
                {
                    throw;
                }
            }
            finally
            {
                filter.OnActionExecuted(action);
            }
        }
    }
}
