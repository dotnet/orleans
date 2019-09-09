using System.Threading;

namespace OneBoxDeployment.Api.StartupTask
{
    /// <summary>
    /// A common context to all asynchronous tasks so a health check can check
    /// when they all have completed and can let the pipeline to start
    /// processing messages.
    /// </summary>
    /// <remarks>Based on code by Andrew Lock at https://andrewlock.net/running-async-tasks-on-app-startup-in-asp-net-core-part-4-using-health-checks/.
    /// See some problems and improvements
    /// <ul>
    ///     <li>https://tools.ietf.org/html/draft-inadarei-api-health-check-02</li>
    ///     <li>https://github.com/aspnet/AspNetCore/issues/5936</li>
    /// </ul>
    /// </remarks>
    public class StartupTaskContext
    {
        private int _outstandingTaskCount = 0;

        /// <summary>
        /// Tbd.
        /// </summary>
        public void RegisterTask()
        {
            Interlocked.Increment(ref _outstandingTaskCount);
        }

        /// <summary>
        /// Tbd.
        /// </summary>
        public void MarkTaskAsComplete()
        {
            Interlocked.Decrement(ref _outstandingTaskCount);
        }


        /// <summary>
        /// Tbd.
        /// </summary>
        public bool IsComplete => _outstandingTaskCount == 0;
    }
}
