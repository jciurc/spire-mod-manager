using Godot;

namespace SpireModManager;

// A container's children put in the order wanted, moving only those out of place: a move is a native call, and the
// container sorts its children again after each. Every redraw puts the main list's rows and headers in order (Vanilla
// View's and Load Order's rows too), most already where they go: the longest run of them already in order stays, and
// each other one goes right after the one before it.
static class ChildOrder
{
#if DEVTOOLS
    // Self-test: how many children Place has moved.
    public static int TestMoved { get; private set; }
#endif

    // first: these children first, in this order; the others after them, in the order they have.
    public static void Place(Node parent, IReadOnlyList<Node> first)
    {
        var children = parent.GetChildren().ToList();
        var wanted = new List<Node>(children.Count);
        var rank = new Dictionary<Node, int>(children.Count);
        foreach (var node in first.Concat(children))
            if (rank.TryAdd(node, wanted.Count))
                wanted.Add(node);
        var stay = InOrder(children.Select(c => rank[c]).ToList()).Select(i => children[i]).ToHashSet();
        if (stay.Count == children.Count)
            return;
        // Placed in turn, each after the one before it (already in place); `children` follows the moves.
        for (var i = 0; i < wanted.Count; i++)
        {
            var node = wanted[i];
            if (stay.Contains(node))
                continue;
            children.Remove(node);
            var to = i == 0 ? 0 : children.IndexOf(wanted[i - 1]) + 1;
            children.Insert(to, node);
            parent.MoveChild(node, to);
#if DEVTOOLS
            TestMoved++;
#endif
        }
    }

    // The positions of a longest run of ranks that only rise (all ranks differ): the children that can stay.
    static IEnumerable<int> InOrder(List<int> ranks)
    {
        var ends = new List<int>(); // for each length, where the run of it ending on the lowest rank ends
        var before = new int[ranks.Count]; // a run's previous position
        for (var i = 0; i < ranks.Count; i++)
        {
            var (low, high) = (0, ends.Count);
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (ranks[ends[middle]] < ranks[i])
                    low = middle + 1;
                else
                    high = middle;
            }
            before[i] = low > 0 ? ends[low - 1] : -1;
            if (low == ends.Count)
                ends.Add(i);
            else
                ends[low] = i;
        }
        for (var i = ends.Count > 0 ? ends[^1] : -1; i >= 0; i = before[i])
            yield return i;
    }
}
