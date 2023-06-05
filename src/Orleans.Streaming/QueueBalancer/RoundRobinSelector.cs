using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Streams
{
    /// <summary>
    /// Selector using round robin algorithm
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal sealed class RoundRobinSelector<T> : IResourceSelector<T>
    {
        private readonly List<T> resources;
        private int lastSelection;
        public RoundRobinSelector(IEnumerable<T> resources)
        {
            // distinct randomly ordered readonly collection
            this.resources = resources.Distinct().OrderBy(_ => Random.Shared.Next()).ToList();
            lastSelection = Random.Shared.Next(this.resources.Count);
        }

        public int Count => resources.Count;

        /// <summary>
        /// Try to select certain count of resources from resource list, which doesn't overlap with existing resources
        /// </summary>
        /// <param name="newSelectionCount"></param>
        /// <param name="existingSelection"></param>
        /// <returns></returns>
        public List<T> NextSelection(int newSelectionCount, List<T> existingSelection)
        {
            var selection = new List<T>(Math.Min(newSelectionCount, resources.Count));
            var tries = 0;
            while (selection.Count < newSelectionCount && tries++ < resources.Count)
            {
                lastSelection = (++lastSelection) % (resources.Count);
                if (!existingSelection.Contains(resources[lastSelection]))
                    selection.Add(resources[lastSelection]);
            }
            return selection;
        }
    }
}
