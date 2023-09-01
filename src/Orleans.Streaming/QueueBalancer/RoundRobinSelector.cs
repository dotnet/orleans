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
            this.lastSelection = Random.Shared.Next(this.resources.Count);
        }

        public int Count => this.resources.Count;

        /// <summary>
        /// Try to select certain count of resources from resource list, which doesn't overlap with existing resources
        /// </summary>
        /// <param name="newSelectionCount"></param>
        /// <param name="existingSelection"></param>
        /// <returns></returns>
        public List<T> NextSelection(int newSelectionCount, List<T> existingSelection)
        {
            var selection = new List<T>(Math.Min(newSelectionCount, this.resources.Count));
            int tries = 0;
            while (selection.Count < newSelectionCount && tries++ < this.resources.Count)
            {
                this.lastSelection = (++this.lastSelection) % (this.resources.Count);
                if (!existingSelection.Contains(this.resources[this.lastSelection]))
                    selection.Add(this.resources[this.lastSelection]);
            }
            return selection;
        }
    }
}
