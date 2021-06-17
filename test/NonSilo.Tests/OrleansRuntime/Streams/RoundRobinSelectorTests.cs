using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using Orleans.Streams;
using System.Data;

namespace UnitTests.OrleansRuntime.Streams
{
    [TestCategory("BVT")]
    public class RoundRobinSelectorTests : ResourceSelectorTestRunner
    {
        private const int ResourceCount = 10;
        private readonly IResourceSelector<string> resourceSelector;
        private readonly List<string> resources;

        public RoundRobinSelectorTests(ITestOutputHelper output) : base(output)
        {
            this.resources = Enumerable.Range(0, ResourceCount).Select(i => $"resource_{i}").ToList();
            this.resourceSelector = new RoundRobinSelector<string>(this.resources);
        }

        [Fact]
        public void NextSelectionWillGoThroughEveryResourceIfExistingSelectionEmptyTest()
        {
            base.NextSelectionWillGoThroughEveryResourceIfExistingSelectionEmpty(resources, resourceSelector);
        }

        [Fact]
        public void NextSelectionWontGoInfinitelyTest()
        {
            base.NextSelectionWontGoInfinitely(resources, resourceSelector);
        }

        [Fact]
        public void NextSelectionWontReSelectExistingSelectionsTest()
        {
            base.NextSelectionWontReSelectExistingSelections(resources, resourceSelector);
        }

        [Fact]
        public void NextSelectionWontReSelectExistingSelectionsWithDuplicatesTest()
        {
            var duplicateResources = new List<string>(this.resources);
            duplicateResources.AddRange(this.resources);
            var resourceSelectorWithDuplicates = new RoundRobinSelector<string>(duplicateResources);
            base.NextSelectionWontReSelectExistingSelections(this.resources, resourceSelectorWithDuplicates);
        }
    }
}
