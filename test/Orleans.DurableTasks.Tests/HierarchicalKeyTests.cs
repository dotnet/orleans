using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Orleans.DurableTasks.Tests
{
    [Trait("Category", "BVT")]
    public class HierarchicalKeyTests
    {
        [Fact]
        public void RepresentationIsInconsequential()
        {
            var aParent = HierarchicalKey.Create("foo/bar");
            var a = aParent.CreateChildKey("baz");
            var b = HierarchicalKey.Create("foo/bar/baz");
            Assert.Equal(a, b);
            Assert.Equal(b.ToString(), b.ToString());
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.Equal(a.ToString().Length, a.Length);
            Assert.Equal(b.ToString().Length, b.Length);
            Assert.Equal(a.Length, b.Length);

            var aSegments = new List<string>();
            foreach (var segment in a)
            {
                aSegments.Add(segment.ToString());
            }

            var bSegments = new List<string>();
            foreach (var segment in b)
            {
                bSegments.Add(segment.ToString());
            }

            Assert.Equal(aSegments.Count, bSegments.Count);

            Assert.Equal(aSegments, bSegments);
        }

        [Fact]
        public void SegmentsCanBeEscaped()
        {
            var aParent = HierarchicalKey.Create("foo/bar\\/");
            var a = aParent.CreateChildKey("baz");
            var b = HierarchicalKey.Create("foo/bar\\//baz");
            Assert.Equal(a, b);
            Assert.Equal(b.ToString(), b.ToString());
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.Equal(a.ToString().Length, a.Length);
            Assert.Equal(b.ToString().Length, b.Length);
            Assert.Equal(a.Length, b.Length);

            var aSegments = new List<string>();
            foreach (var segment in a)
            {
                aSegments.Add(segment.ToString());
            }

            var bSegments = new List<string>();
            foreach (var segment in b)
            {
                bSegments.Add(segment.ToString());
            }

            Assert.Equal(aSegments.Count, bSegments.Count);

            Assert.Equal(aSegments, bSegments);
        }
        
        [Fact]
        public void OnlyValidValuesAreAllowed()
        {
            Assert.Throws<ArgumentNullException>(() => HierarchicalKey.Create(null));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(""));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("/"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("//"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("a//"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("//a"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("\\//"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("a/b//c/d"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("aaa/bbb//ccc/ddd"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("a/b/c/d//"));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("//a/b/c/d//"));
            _ = HierarchicalKey.Create("\\/\\/");
            _ = HierarchicalKey.Create("aaa/bbb/ccc/ddd");
            _ = HierarchicalKey.Create("a/b/c/d");
            _ = HierarchicalKey.Create("\\/\\/a/b/c/d\\/\\/");
        }

        [Fact]
        public void GetParentTest()
        {
            var aKey = HierarchicalKey.Create("aaa");

            Assert.True(aKey.IsParentOf(HierarchicalKey.Create(aKey, "bbb")));
            Assert.True(aKey.IsAncestorOf(HierarchicalKey.Create("aaa/bbb/ccc")));
            Assert.True(aKey.IsParentOf(HierarchicalKey.Create("aaa/bbb")));
            Assert.False(aKey.IsParentOf(HierarchicalKey.Create("aaa/bbb/ccc")));
            Assert.False(aKey.IsAncestorOf(HierarchicalKey.Create("bbb/ccc")));
            Assert.False(HierarchicalKey.Create("a").IsAncestorOf(HierarchicalKey.Create("aa")));

            Assert.True(aKey.IsAncestorOf(aKey));
            Assert.False(aKey.IsParentOf(aKey));
            Assert.False(aKey.IsParentOf(HierarchicalKey.Create("aaa")));
            Assert.False(aKey.IsParentOf(HierarchicalKey.Create("bbb")));

            Assert.Null(aKey.GetParent());
            Assert.Same(aKey, HierarchicalKey.Create(aKey, "bbb").GetParent());
            Assert.True(HierarchicalKey.Create("aaa/bbb").IsChildOf(aKey));
            Assert.False(HierarchicalKey.Create("aaa/bbb/ccc").IsChildOf(aKey));
            Assert.Equal(HierarchicalKey.Create(aKey, "bbb"), HierarchicalKey.Create("aaa/bbb/ccc").GetParent());
            Assert.True(HierarchicalKey.Create("aaa/bbb").IsParentOf(HierarchicalKey.Create("aaa/bbb/ccc")));
            Assert.Equal(HierarchicalKey.Create("aaa/bbb"), HierarchicalKey.Create("aaa/bbb/ccc").GetParent());

            Assert.Null(HierarchicalKey.Create("\\/\\/").GetParent());
            Assert.Null(HierarchicalKey.Create("\\/").GetParent());
            Assert.Equal(HierarchicalKey.Create("\\/\\/"), HierarchicalKey.Create("\\/\\//aaa").GetParent());
            Assert.Equal(HierarchicalKey.Create("\\/"), HierarchicalKey.Create("\\//\\/").GetParent());
        }

        [Fact]
        public void CreateEscapedChildKeyTest()
        {
            var aParent = HierarchicalKey.Create("foo/bar\\/");
            var a = aParent.CreateEscapedChildKey("baz/boz");
            var b = aParent.CreateEscapedChildKey("baz\\/boz");
            Assert.Equal(a, b);

            Assert.Equal(a, b);
            Assert.Equal(b.ToString(), b.ToString());
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.Equal(a.ToString().Length, a.Length);
            Assert.Equal(b.ToString().Length, b.Length);
            Assert.Equal(a.Length, b.Length);

            var aSegments = new List<string>();
            foreach (var segment in a)
            {
                aSegments.Add(segment.ToString());
            }

            var bSegments = new List<string>();
            foreach (var segment in b)
            {
                bSegments.Add(segment.ToString());
            }

            Assert.Equal(aSegments.Count, bSegments.Count);

            Assert.Equal(aSegments, bSegments);
        }
    }
}
