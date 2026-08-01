// =============================================================================
// TAG TRACKER SAMPLE: Demonstrating IDurableSet<T>
// =============================================================================
//
// This sample shows how to use IDurableSet<T> - a persistent unique collection
// that survives grain restarts and crashes.
//
// KEY CONCEPTS:
// - IDurableSet<T> stores unique items only (no duplicates)
// - Supports Add, Remove, Contains, and set operations
// - All modifications are journaled for durability
// - Perfect for: tags, categories, unique IDs, membership tracking
//
// =============================================================================

using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.TagTracker;

/// <summary>
/// A durable tag tracking system that persists tags across restarts.
/// </summary>
internal static class TagTracker
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();

        Console.WriteLine("--- TagTracker Sample: Demonstrating IDurableSet<T> ---");
        Console.WriteLine();

        // Get a tag tracker grain for a specific article
        var article = grainFactory.GetGrain<ITaggedItemGrain>("article-123");

        // Clear existing tags for demo purposes
        await article.ClearTagsAsync();

        // Add some tags
        await article.AddTagAsync("orleans");
        await article.AddTagAsync("distributed-systems");
        await article.AddTagAsync("dotnet");
        await article.AddTagAsync("cloud");

        Console.WriteLine("Added 4 tags:");
        await PrintTags(article);

        // Try to add a duplicate - should be ignored
        var added = await article.AddTagAsync("orleans");
        Console.WriteLine($"\nTried to add 'orleans' again. Added: {added} (should be false)");

        // Check if specific tags exist
        Console.WriteLine($"\nHas 'dotnet' tag: {await article.HasTagAsync("dotnet")}");
        Console.WriteLine($"Has 'java' tag: {await article.HasTagAsync("java")}");

        // Remove a tag
        var removed = await article.RemoveTagAsync("cloud");
        Console.WriteLine($"\nRemoved 'cloud' tag: {removed}");

        Console.WriteLine("\nFinal tags:");
        await PrintTags(article);

        // Get tag count
        var count = await article.GetTagCountAsync();
        Console.WriteLine($"\nTotal tags: {count}");

        Console.WriteLine("\nTagTracker sample completed!");
        Console.WriteLine("Note: Tags persist across restarts.\n");
    }

    private static async Task PrintTags(ITaggedItemGrain item)
    {
        var tags = await item.GetAllTagsAsync();
        Console.WriteLine($"  Tags: [{string.Join(", ", tags)}]");
    }

    // -------------------------------------------------------------------------
    // GRAIN INTERFACE
    // -------------------------------------------------------------------------

    [Alias("WorkflowsApp.Service.Samples.TagTracker.ITaggedItemGrain")]
    public interface ITaggedItemGrain : IGrainWithStringKey
    {
        /// <summary>Adds a tag. Returns false if the tag already exists.</summary>
        [Alias("AddTagAsync")]
        ValueTask<bool> AddTagAsync(string tag);

        /// <summary>Removes a tag. Returns false if the tag didn't exist.</summary>
        [Alias("RemoveTagAsync")]
        ValueTask<bool> RemoveTagAsync(string tag);

        /// <summary>Checks if a tag exists.</summary>
        [Alias("HasTagAsync")]
        ValueTask<bool> HasTagAsync(string tag);

        /// <summary>Gets all tags.</summary>
        [Alias("GetAllTagsAsync")]
        ValueTask<List<string>> GetAllTagsAsync();

        /// <summary>Gets the number of tags.</summary>
        [Alias("GetTagCountAsync")]
        ValueTask<int> GetTagCountAsync();

        /// <summary>Removes all tags.</summary>
        [Alias("ClearTagsAsync")]
        ValueTask ClearTagsAsync();

        /// <summary>Adds multiple tags at once.</summary>
        [Alias("AddTagsAsync")]
        ValueTask<int> AddTagsAsync(IEnumerable<string> tags);
    }

    // -------------------------------------------------------------------------
    // GRAIN IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// A grain that maintains durable tags using IDurableSet.
    ///
    /// HOW IT WORKS:
    /// 1. IDurableSet<string> is injected via [FromKeyedServices]
    /// 2. The key "tags" identifies this set in the grain's journal
    /// 3. Add/Remove operations are journaled (duplicates are ignored)
    /// 4. WriteStateAsync() commits changes to storage
    /// 5. On restart, the set is reconstructed from the journal
    ///
    /// WHY USE A SET?
    /// - Automatic duplicate prevention
    /// - Fast O(1) Contains/Add/Remove operations
    /// - Set theory operations (union, intersection, etc.)
    /// </summary>
    internal class TaggedItemGrain(
        [FromKeyedServices("tags")] IDurableSet<string> tags)
        : DurableGrain, ITaggedItemGrain
    {
        private readonly IDurableSet<string> _tags = tags;

        public async ValueTask<bool> AddTagAsync(string tag)
        {
            // Normalize tag to lowercase for consistency
            tag = tag.ToLowerInvariant();

            // Add returns false if the item already exists
            if (_tags.Add(tag))
            {
                await WriteStateAsync();
                return true;
            }

            return false;
        }

        public async ValueTask<bool> RemoveTagAsync(string tag)
        {
            tag = tag.ToLowerInvariant();

            if (_tags.Remove(tag))
            {
                await WriteStateAsync();
                return true;
            }

            return false;
        }

        public ValueTask<bool> HasTagAsync(string tag)
        {
            tag = tag.ToLowerInvariant();
            return new(_tags.Contains(tag));
        }

        public ValueTask<List<string>> GetAllTagsAsync()
        {
            return new(_tags.ToList());
        }

        public ValueTask<int> GetTagCountAsync()
        {
            return new(_tags.Count);
        }

        public async ValueTask ClearTagsAsync()
        {
            _tags.Clear();
            await WriteStateAsync();
        }

        public async ValueTask<int> AddTagsAsync(IEnumerable<string> tags)
        {
            var addedCount = 0;

            foreach (var tag in tags)
            {
                if (_tags.Add(tag.ToLowerInvariant()))
                {
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                await WriteStateAsync();
            }

            return addedCount;
        }
    }
}
