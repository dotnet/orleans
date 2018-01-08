using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Threading
{
    internal class ActionFilter<T> where T : IExecutable
    {
        protected static readonly Action<T> NoOpFilter = _ => { };

        protected static readonly Func<Exception, T, bool> NoOpHandler = (e, c) => false;

        public virtual Action<T> OnActionExecuting { get; } = NoOpFilter;

        public virtual Action<T> OnActionExecuted { get; } = NoOpFilter;

        public virtual Func<Exception, T, bool> ExceptionHandler { get; } = NoOpHandler;
    }

    internal class ActionLambdaFilter<T> : ActionFilter<T> where T : IExecutable
    {
        public ActionLambdaFilter(
            Action<T> onActionExecuting = null,
            Action<T> onActionExecuted = null,
            Func<Exception, T, bool> exceptionHandler = null)
        {
            if (onActionExecuting == null && onActionExecuted == null && exceptionHandler == null)
            {
                throw new ArgumentNullException("Lambda filter requires at least one non-null parameter to be functional");
            }

            OnActionExecuting = onActionExecuting ?? NoOpFilter;
            OnActionExecuted = onActionExecuted ?? NoOpFilter;
            ExceptionHandler = exceptionHandler ?? NoOpHandler;
        }

        public sealed override Action<T> OnActionExecuting { get; }

        public sealed override Action<T> OnActionExecuted { get; }

        public sealed override Func<Exception, T, bool> ExceptionHandler { get; }
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
