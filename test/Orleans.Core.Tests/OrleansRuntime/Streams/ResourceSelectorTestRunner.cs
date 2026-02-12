using Xunit.Abstractions;
using Xunit;
using Orleans.Streams;

namespace UnitTests.OrleansRuntime.Streams
{
    public abstract class ResourceSelectorTestRunner
    {
        private readonly ITestOutputHelper output;

        protected ResourceSelectorTestRunner(ITestOutputHelper output)
        {
            this.output = output;
        }

        internal void NextSelectionWillGoThroughEveryResourceIfExistingSelectionEmpty(List<string> resources, IResourceSelector<string> resourceSelector)
        {
            Assert.Equal(resources.Distinct().Count(), resources.Count);
            resources.Sort();
            List<string> selected = resourceSelector.NextSelection(resources.Count, new List<string>());
            Assert.Equal(resources.Count, selected.Count);
            selected = selected.Distinct().ToList();
            selected.Sort();
            Assert.Equal(resources.Count, selected.Count);
            for (int i=0; i<selected.Count; i++)
            {
                Assert.Equal(resources[i], selected[i]);
            }
        }

        internal void NextSelectionWontGoInfinitely(List<string> resources, IResourceSelector<string> resourceSelector)
        {
            Assert.Equal(resources.Distinct().Count(), resources.Count);
            resources.Sort();
            List<string> selected = resourceSelector.NextSelection(int.MaxValue, new List<string>());
            Assert.Equal(resources.Count, selected.Count);
            selected = selected.Distinct().ToList();
            selected.Sort();
            Assert.Equal(resources.Count, selected.Count);
            for (int i = 0; i < selected.Count; i++)
            {
                Assert.Equal(resources[i], selected[i]);
            }
        }

        internal void NextSelectionWontReSelectExistingSelections(List<string> resources, IResourceSelector<string> resourceSelector)
        {
            Assert.Equal(resources.Distinct().Count(), resources.Count);
            for (int selectCount = 0; selectCount < resources.Count; selectCount++)
            {
                for (int excludeCount = 0; excludeCount < resources.Count; excludeCount++)
                {
                    List<string> excluded = resourceSelector.NextSelection(excludeCount, new List<string>());
                    Assert.Equal(excludeCount, excluded.Count);
                    excluded = excluded.Distinct().ToList();
                    Assert.Equal(excludeCount, excluded.Count);

                    List<string> selected = resourceSelector.NextSelection(selectCount, excluded);
                    int expectedCount = Math.Min(selectCount, resources.Count - excludeCount);
                    Assert.Equal(expectedCount, selected.Count);
                    selected = selected.Distinct().ToList();
                    Assert.Equal(expectedCount, selected.Count);
                    for (int i = 0; i < selected.Count; i++)
                    {
                        Assert.DoesNotContain(selected[i], excluded);
                    }
                }
            }
        }
    }
}
