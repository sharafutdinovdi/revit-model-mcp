using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitModelMcp.Capture;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Activity;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitModelMcp.Activity;

/// <summary>
/// Collects the exact elements one MCP job changes, from <c>Application.DocumentChanged</c> for the job's
/// target document. Jobs run one at a time on the Revit API thread, so every transaction committed in the
/// target document while the capture is open belongs to the job, including Revit-named ones such as the
/// transaction <c>LoadFamily</c> opens. Dry runs commit inside a transaction group and roll the group back,
/// so their would-be changes are reported too; elements a dry run creates are described while they exist.
/// The per-id set arithmetic (an id added and later deleted within the job cancels out; an id that ends up
/// Created or Deleted drops out of Changed) lives in the Revit-independent <see cref="ChangeMerge"/>.
/// </summary>
internal sealed class ChangeCapture : IDisposable
{
    public const int MaxRefs = 5000;

    private readonly RevitApplication _application;
    private readonly Document _document;
    private readonly ChangeMerge _merge = new();
    private readonly HashSet<long> _noise = new();
    private readonly Dictionary<long, ActivityElementRef> _described = new();

    private ChangeCapture(RevitApplication application, Document document)
    {
        _application = application;
        _document = document;
        UndoStateAtStart = UndoTracker.Snapshot();
        _application.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Undo tracking state before the job, restored when the job's changes are rolled back.</summary>
    public (string? DocumentTitle, string? LastTransactionName) UndoStateAtStart { get; }

    public static ChangeCapture Start(RevitApplication application, Document document) => new(application, document);

    public void Dispose() => _application.DocumentChanged -= OnDocumentChanged;

    /// <summary>Writes the captured element lists (first <see cref="MaxRefs"/> of each) and true totals to the entry.</summary>
    public void Fill(ActivityEntry entry)
    {
        entry.Changed = Refs(ChangeKind.Changed, out var changedTotal);
        entry.Created = Refs(ChangeKind.Created, out var createdTotal);
        entry.Deleted = Refs(ChangeKind.Deleted, out var deletedTotal);
        entry.ChangedTotal = changedTotal;
        entry.CreatedTotal = createdTotal;
        entry.DeletedTotal = deletedTotal;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs arguments)
    {
        if (arguments.Operation != UndoOperation.TransactionCommitted) return;
        if (!_document.Equals(arguments.GetDocument())) return;
        foreach (var id in arguments.GetAddedElementIds()) Added(id);
        foreach (var id in arguments.GetModifiedElementIds()) Modified(id);
        foreach (var id in arguments.GetDeletedElementIds()) Deleted(RevitValueReader.GetId(id));
    }

    private void Added(ElementId id)
    {
        var value = RevitValueReader.GetId(id);
        var element = _document.GetElement(id);
        // Always tracked, even when noise, so a later delete of the same id in this job (Revit creates
        // and deletes transient, often categoryless, helper elements inside some jobs) cancels it out.
        _merge.Add(value);
        if (IsNoise(element))
        {
            _noise.Add(value);
            return;
        }
        // A dry run rolls created elements back before the entry is recorded, so describe them now.
        if (_described.Count < MaxRefs * 3 && element is not null) _described[value] = Describe(value, element);
    }

    private void Modified(ElementId id)
    {
        var value = RevitValueReader.GetId(id);
        if (_merge.KindOf(value) != ChangeKind.None) return;
        if (IsNoise(_document.GetElement(id)))
        {
            _noise.Add(value);
            return;
        }
        _merge.Modify(value);
    }

    private void Deleted(long value) => _merge.Delete(value);

    private List<ActivityElementRef> Refs(ChangeKind kind, out int total)
    {
        var refs = new List<ActivityElementRef>();
        total = 0;
        foreach (var value in _merge.Ids(kind))
        {
            // Deletions of noise elements never added or modified in this job cannot be checked for
            // category (the element is already gone) and are reported as before; noise elements that were
            // created or changed in this job stay hidden from the counts and lists.
            if (kind != ChangeKind.Deleted && _noise.Contains(value)) continue;
            total++;
            if (refs.Count >= MaxRefs) continue;
            if (!_described.TryGetValue(value, out var reference))
            {
                var element = _document.GetElement(ActionCommandExecutor.CreateId(value));
                reference = element is null ? new ActivityElementRef { Id = value } : Describe(value, element);
            }
            refs.Add(reference);
        }
        return refs;
    }

    /// <summary>Skips internal bookkeeping elements; views, sheets, levels and grids stay listed.</summary>
    private static bool IsNoise(Element? element) =>
        element is not null && element.Category is null && element is not (View or Level or Grid);

    private static ActivityElementRef Describe(long value, Element element)
    {
        string? name;
        try
        {
            name = element is FamilyInstance { Symbol: { } symbol } ? $"{symbol.FamilyName}: {symbol.Name}" : element.Name;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            name = null;
        }
        return new ActivityElementRef
        {
            Id = value,
            Category = element.Category?.Name,
            Name = string.IsNullOrEmpty(name) ? null : name
        };
    }
}
