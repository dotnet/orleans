using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Indexes the identifiers of stored tasks by hierarchy, so a task's nearest stored descendants are found without
/// scanning every task.
/// </summary>
/// <remarks>
/// Every identifier which is stored, or which has stored descendants, is linked into its parent's child set. An
/// identifier between a task and its nearest stored descendants, such as a combinator's operation identifier, is linked
/// without being stored. Adding or removing a task costs time proportional to its depth, and the index holds one child
/// set per identifier with linked children.
/// </remarks>
internal sealed class DurableTaskHierarchy
{
    private readonly Dictionary<TaskId, HashSet<TaskId>> _children = [];

    /// <summary>Creates an index of <paramref name="taskIds"/>.</summary>
    public static DurableTaskHierarchy Create(IEnumerable<TaskId> taskIds)
    {
        var result = new DurableTaskHierarchy();
        foreach (var taskId in taskIds)
        {
            result.Add(taskId);
        }

        return result;
    }

    /// <summary>Links a newly stored task.</summary>
    public void Add(TaskId taskId)
    {
        for (var child = taskId; ;)
        {
            var parent = child.Parent();
            if (parent.IsDefault)
            {
                return;
            }

            // An identifier with linked children is itself linked, so the walk ends at the first one.
            ref var children = ref CollectionsMarshal.GetValueRefOrAddDefault(_children, parent, out var parentLinked);
            children ??= [];
            if (!children.Add(child) || parentLinked)
            {
                return;
            }

            child = parent;
        }
    }

    /// <summary>Unlinks a task which is no longer stored, keeping it linked while it has stored descendants.</summary>
    /// <param name="taskId">The removed task.</param>
    /// <param name="isStored">Returns whether an identifier is stored.</param>
    public void Remove(TaskId taskId, Func<TaskId, bool> isStored)
    {
        for (var child = taskId; !_children.ContainsKey(child);)
        {
            var parent = child.Parent();
            if (parent.IsDefault)
            {
                return;
            }

            var found = _children.TryGetValue(parent, out var children);
            Debug.Assert(found && children!.Contains(child), "Every stored task is linked into its parent's child set.");
            children!.Remove(child);
            if (children.Count > 0)
            {
                return;
            }

            _children.Remove(parent);
            if (isStored(parent))
            {
                return;
            }

            child = parent;
        }
    }

    /// <summary>Removes every task.</summary>
    public void Clear() => _children.Clear();

    /// <summary>Gets the stored tasks whose nearest stored ancestor is <paramref name="taskId"/>.</summary>
    /// <param name="taskId">The ancestor.</param>
    /// <param name="isStored">Returns whether an identifier is stored.</param>
    public IEnumerable<TaskId> GetNearestStoredDescendants(TaskId taskId, Func<TaskId, bool> isStored)
    {
        if (!_children.TryGetValue(taskId, out var children))
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (isStored(child))
            {
                yield return child;
                continue;
            }

            Debug.Assert(_children.ContainsKey(child), "An identifier which is not stored is linked only while it has stored descendants.");
            foreach (var descendant in GetNearestStoredDescendants(child, isStored))
            {
                yield return descendant;
            }
        }
    }
}
