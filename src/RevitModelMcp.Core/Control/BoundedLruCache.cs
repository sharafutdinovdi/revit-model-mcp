namespace RevitModelMcp.Core.Control;

/// <summary>Least recently used cache with a fixed capacity. Not thread safe: used on the Revit thread.</summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _nodes = [];
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public BoundedLruCache(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _nodes.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_nodes.TryGetValue(key, out var existing)) _order.Remove(existing);
        var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
        _order.AddFirst(node);
        _nodes[key] = node;
        if (_nodes.Count <= _capacity) return;
        var oldest = _order.Last!;
        _order.RemoveLast();
        _nodes.Remove(oldest.Value.Key);
    }
}
