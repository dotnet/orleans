using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Threading
{
    internal class ActionFilter<T> where T : IExecutable
    {
        private static readonly Action<T> NoOpFilter = _ => { };

        private static readonly Func<Exception, T, bool> NoOpHandler = (e, c) => false;

        public ActionFilter(
            Action<T> onActionExecuting = null,
            Action<T> onActionExecuted = null,
            Func<Exception, T, bool> exceptionHandler = null)
        {
            OnActionExecuting = onActionExecuting ?? NoOpFilter;
            OnActionExecuted = onActionExecuted ?? NoOpFilter;
            ExceptionHandler = exceptionHandler ?? NoOpHandler;
        }

        public virtual Action<T> OnActionExecuting { get; }

        public virtual Action<T> OnActionExecuted { get; }

        public virtual Func<Exception, T, bool> ExceptionHandler { get; }
    }

    internal class ExecutionFilter : ActionFilter<ExecutionContext>
    {
        public ExecutionFilter(
            Action<ExecutionContext> onActionExecuting = null,
            Action<ExecutionContext> onActionExecuted = null,
            Func<Exception, ExecutionContext, bool> exceptionHandler = null)
            : base(onActionExecuting, onActionExecuted, exceptionHandler)
        {
        }
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
