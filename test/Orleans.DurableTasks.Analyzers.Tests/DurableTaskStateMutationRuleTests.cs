using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the rule which reports changes made in place to values which grain state holds.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskStateMutationRuleTests
{
    [Fact]
    public Task StateValuesChangeThroughAssignment() => AnalyzerVerifier.VerifyAsync("""
        public sealed class Order
        {
            public List<string> Lines { get; } = new();
            public int Count { get; set; }
            public Order? Parent { get; set; }
            public int[] Totals { get; } = new int[1];
        }

        public sealed record Snapshot(System.Collections.Immutable.ImmutableList<string> Lines, int Count);

        public class C
        {
            private readonly IDurableValue<Order> _order = null!;
            private readonly IDurableValue<Snapshot> _snapshot = null!;
            private readonly IDurableValue<int> _count = null!;
            private readonly Orleans.Runtime.IPersistentState<Order> _state = null!;
            private readonly IDurableDictionary<string, Order> _orders = null!;
            private readonly IDurableList<Order> _list = null!;

            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            public async DurableTask RunAsync(string line)
            {
                DurableTask.RunInline("effect", () => {|ORLEANS0123:_order.Value!.Count = 4|});
                await Step(1).WithId("step");

                {|ORLEANS0123:_order.Value!.Lines.Add(line)|};
                {|ORLEANS0123:_order.Value!.Count++|};
                {|ORLEANS0123:_order.Value!.Totals[0] = 1|};
                {|ORLEANS0123:_state.State.Count = 2|};
                {|ORLEANS0123:_orders["a"].Count += 1|};
                {|ORLEANS0123:_list[0].Parent!.Count = 3|};
                var alias = _order.Value!;
                {|ORLEANS0123:alias.Lines.Clear()|};

                _order.Value = new Order();
                _snapshot.Value = _snapshot.Value! with { Lines = _snapshot.Value.Lines.Add(line), Count = 5 };
                _count.Value++;
                _state.State = new Order { Count = 6 };
                _orders["b"] = new Order();
                _list.Add(new Order());
                var copy = new Order();
                copy.Lines.Add(line);
                var replaced = _order.Value!;
                replaced = copy;
                replaced.Count = 7;
            }

            public void Ordinary(string line) => _order.Value!.Lines.Add(line);
        }
        """);
}
