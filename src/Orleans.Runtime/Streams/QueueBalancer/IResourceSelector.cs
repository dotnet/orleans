using System.Collections.Generic;

namespace Orleans.Streams
{
    /// <summary>
    /// IResourceSelector selects a certain amount of resources from a resource list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal interface IResourceSelector<T>
    {
        /// <summary>
        /// Number of resources
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Try to select certain count of resources from resource list, which doesn't overlap with existing selection
        /// </summary>
        /// <param name="newSelectionCount"></param>
        /// <param name="existingSelection"></param>
        /// <returns></returns>
        List<T> NextSelection(int newSelectionCount, List<T> existingSelection);
    }
}
