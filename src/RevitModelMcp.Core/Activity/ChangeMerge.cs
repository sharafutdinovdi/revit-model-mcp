namespace RevitModelMcp.Core.Activity;

/// <summary>Final classification of one element id within a job's collected <see cref="ChangeMerge"/>.</summary>
public enum ChangeKind
{
    /// <summary>Cancelled out: the id was added and later deleted within the same job.</summary>
    None,
    Changed,
    Created,
    Deleted
}

/// <summary>
/// Pure set arithmetic that merges one job's added/modified/deleted element ids, observed in commit order,
/// into a single final <see cref="ChangeKind"/> per id. Revit occasionally creates and deletes transient
/// helper ids inside one job (for example while aligning datums to a link with missing levels created on
/// demand); an id added and later deleted within the job cancels out and is dropped from every list. An id
/// that ends up Created or Deleted is dropped from Changed, and every id is classified exactly once.
/// </summary>
public sealed class ChangeMerge
{
    private readonly Dictionary<long, ChangeKind> _kinds = new();
    private readonly List<long> _order = [];

    /// <summary>The final kind recorded for <paramref name="id"/>, or <see cref="ChangeKind.None"/> if untracked or cancelled.</summary>
    public ChangeKind KindOf(long id) => _kinds.TryGetValue(id, out var kind) ? kind : ChangeKind.None;

    /// <summary>Records an id added in this job. Always classifies the id as Created, overwriting any earlier kind.</summary>
    public void Add(long id) => Set(id, ChangeKind.Created);

    /// <summary>Records an id modified in this job. Only classifies as Changed the first time the id is seen.</summary>
    public void Modify(long id)
    {
        if (_kinds.TryGetValue(id, out var kind) && kind != ChangeKind.None) return;
        Set(id, ChangeKind.Changed);
    }

    /// <summary>
    /// Records an id deleted in this job. An id created earlier in the same job cancels out to
    /// <see cref="ChangeKind.None"/>; any other id (previously Changed, previously Deleted, or unseen)
    /// becomes Deleted.
    /// </summary>
    public void Delete(long id)
    {
        if (_kinds.TryGetValue(id, out var kind) && kind == ChangeKind.Created)
        {
            Set(id, ChangeKind.None);
            return;
        }
        Set(id, ChangeKind.Deleted);
    }

    /// <summary>Every tracked id with final kind <paramref name="kind"/>, in first-seen order.</summary>
    public IReadOnlyList<long> Ids(ChangeKind kind)
    {
        var result = new List<long>();
        foreach (var id in _order)
            if (_kinds[id] == kind)
                result.Add(id);
        return result;
    }

    private void Set(long id, ChangeKind kind)
    {
        if (!_kinds.ContainsKey(id)) _order.Add(id);
        _kinds[id] = kind;
    }
}
