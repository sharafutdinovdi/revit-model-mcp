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
/// </summary>
internal sealed class ChangeCapture : IDisposable
{
    public const int MaxRefs = 5000;

    private readonly RevitApplication _application;
    private readonly Document _document;
    private readonly Dictionary<long, Kind> _kinds = new();
    private readonly List<long> _order = [];
    private readonly Dictionary<long, ActivityElementRef> _described = new();

    private ChangeCapture(RevitApplication application, Document document)
    {
        _application = application;
        _document = document;
        UndoStateAtStart = UndoTracker.Snapshot();
        _application.DocumentChanged += OnDocumentChanged;
    }

    private enum Kind
    {
        None,
        Changed,
        Created,
        Deleted
    }

    /// <summary>Undo tracking state before the job, restored when the job's changes are rolled back.</summary>
    public (string? DocumentTitle, string? LastTransactionName) UndoStateAtStart { get; }

    public static ChangeCapture Start(RevitApplication application, Document document) => new(application, document);

    public void Dispose() => _application.DocumentChanged -= OnDocumentChanged;

    /// <summary>Writes the captured element lists (first <see cref="MaxRefs"/> of each) and true totals to the entry.</summary>
    public void Fill(ActivityEntry entry)
    {
        entry.Changed = Refs(Kind.Changed, out var changedTotal);
        entry.Created = Refs(Kind.Created, out var createdTotal);
        entry.Deleted = Refs(Kind.Deleted, out var deletedTotal);
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
        var element = _document.GetElement(id);
        if (IsNoise(element)) return;
        var value = RevitValueReader.GetId(id);
        Set(value, Kind.Created);
        // A dry run rolls created elements back before the entry is recorded, so describe them now.
        if (_described.Count < MaxRefs * 3 && element is not null) _described[value] = Describe(value, element);
    }

    private void Modified(ElementId id)
    {
        var value = RevitValueReader.GetId(id);
        if (_kinds.TryGetValue(value, out var kind) && kind != Kind.None) return;
        if (IsNoise(_document.GetElement(id))) return;
        Set(value, Kind.Changed);
    }

    private void Deleted(long value)
    {
        if (_kinds.TryGetValue(value, out var kind) && kind == Kind.Created)
        {
            Set(value, Kind.None);
            return;
        }
        Set(value, Kind.Deleted);
    }

    private void Set(long value, Kind kind)
    {
        if (!_kinds.ContainsKey(value)) _order.Add(value);
        _kinds[value] = kind;
    }

    private List<ActivityElementRef> Refs(Kind kind, out int total)
    {
        var refs = new List<ActivityElementRef>();
        total = 0;
        foreach (var value in _order)
        {
            if (_kinds[value] != kind) continue;
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
