using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class DocumentActions
{
    private static readonly DocumentIdentityComparer<Document> Identity = new((first, second) => first.Equals(second));
    private static readonly Dictionary<Document, (string Mode, string OriginalPath)> Opened =
        new(Identity);

    internal static ActionResultData Execute(UIApplication application, string command, ActionJobContract action)
    {
        if (command == "open-document") return Open(application, action);
        if (command == "new-document") return New(application, action);
        var document = ActionCommandExecutor.ResolveDocument(application, action.Document);
        if (document.IsModifiable) throw new InvalidOperationException("The document has an open transaction.");
        return command switch
        {
            "activate-document" => Activate(application, document),
            "activate-view" => ActivateView(application, document, action),
            "close-views" => CloseViews(application, document, action),
            "close-document" => Close(application, document, action),
            "save-document" => Save(application, document, action),
            "sync-document" => Sync(document, action),
            _ => throw new ArgumentException($"Unknown document command: {command}.")
        };
    }

    internal static List<DocumentState> List(UIApplication application, bool includeLinked) =>
        application.Application.Documents.Cast<Document>()
            .Where(document => includeLinked || !document.IsLinked)
            .Select(document => new DocumentState
            {
                Title = document.Title,
                Path = document.PathName,
                IsActive = document.Equals(application.ActiveUIDocument?.Document),
                IsLinked = document.IsLinked,
                IsFamilyDocument = document.IsFamilyDocument,
                IsWorkshared = document.IsWorkshared,
                IsDetached = document.IsDetached,
                IsModified = document.IsModified,
                OpenedByMcp = Opened.ContainsKey(document),
                CentralPath = CentralPath(document)
            }).ToList();

    internal static UiSessionState UiState(UIApplication application)
    {
        var uiDocument = application.ActiveUIDocument;
        var document = uiDocument?.Document;
        var view = uiDocument?.ActiveView;
        var selected = uiDocument?.Selection.GetElementIds().ToList() ?? [];
        return new UiSessionState
        {
            ActiveDocument = document is null ? null : new UiDocumentState
            {
                Title = document.Title,
                Path = document.PathName,
                IsFamilyDocument = document.IsFamilyDocument,
                IsWorkshared = document.IsWorkshared,
                IsModified = document.IsModified
            },
            ActiveView = view is null ? null : new UiViewState
            {
                Id = RevitValueReader.GetId(view.Id),
                Name = view.Name,
                Type = view.ViewType.ToString(),
                IsActive = true
            },
            OpenViews = uiDocument?.GetOpenUIViews().Select(item => document!.GetElement(item.ViewId) as View)
                .Where(item => item is not null).Select(item => new UiViewState
                {
                    Id = RevitValueReader.GetId(item!.Id),
                    Name = item.Name,
                    Type = item.ViewType.ToString(),
                    IsActive = item.Id == view!.Id
                }).ToList() ?? [],
            Selection = new UiSelectionState
            {
                Count = selected.Count,
                Elements = selected.Take(500).Select(id => document!.GetElement(id)).Where(item => item is not null)
                    .Select(item => new UiSelectedElement
                    {
                        Id = RevitValueReader.GetId(item!.Id),
                        Category = item.Category?.Name,
                        Name = item.Name
                    }).ToList()
            },
            Documents = List(application, false)
        };
    }

    private static ActionResultData Open(UIApplication application, ActionJobContract action, bool batch = false,
        Action<Document>? capture = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var path = action.DocumentPath!;
        DocumentPathValidator.Validate(path);
        var linkHost = FindLinkHost(application, path);
        if (linkHost is not null) throw LinkedDocumentError(path, linkHost);
        var server = path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase);
        if (server && !ModelPathUtils.IsValidUserVisibleFullServerPath(path))
            throw new ArgumentException("Invalid Revit Server model path.");
        var sourcePath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        using var fileInfo = !server && path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
            ? BasicFileInfo.Extract(path) : null;
        var isCentral = server || fileInfo?.IsCentral == true;
        var isWorkshared = server || fileInfo?.IsWorkshared == true;
        if (isCentral && action.Mode == "read_only_local")
            throw new InvalidOperationException("read_only_local requires a non-central local file.");
        if (action.Mode == "local_copy" && !isCentral)
            throw new InvalidOperationException("local_copy requires a workshared central model.");
        if (action.Mode == "read_only_local" && !new FileInfo(path).IsReadOnly)
            throw new InvalidOperationException("read_only_local requires a read-only file.");
        var openPath = sourcePath;
        if (action.Mode == "local_copy")
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "locals");
            Directory.CreateDirectory(directory);
            var name = Path.GetFileNameWithoutExtension(path.Replace('/', '\\'));
            var user = new string(application.Application.Username.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
            var local = Path.Combine(directory, $"{name}_{user}.rvt");
            if (File.Exists(local)) throw new IOException($"Local copy already exists: {local}");
            openPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(local);
            WorksharingUtils.CreateNewLocal(sourcePath, openPath);
        }
        var options = new OpenOptions
        {
            Audit = action.Audit,
            DetachFromCentralOption = batch ? DetachFromCentralOption.DetachAndPreserveWorksets : isCentral && action.Mode.StartsWith("detached", StringComparison.Ordinal)
                ? action.Mode == "detached_discard_worksets"
                    ? DetachFromCentralOption.DetachAndDiscardWorksets
                    : DetachFromCentralOption.DetachAndPreserveWorksets
                : DetachFromCentralOption.DoNotDetach
        };
        var requestedWorksets = action.WorksetsOpenNames ?? action.WorksetsCloseNames;
        var ignoreWorksets = !isWorkshared && action.Worksets != "all";
        if (ignoreWorksets && requestedWorksets is { Count: > 0 } &&
            requestedWorksets.All(name => name.IndexOfAny(['*', '?']) < 0))
            throw new InvalidOperationException("Model is not workshared.");
        var unmatched = new List<string>();
        if (!ignoreWorksets)
        {
            var (configuration, patternsUnmatched) = WorksetConfiguration(openPath, action.Worksets, requestedWorksets);
            options.SetOpenWorksetsConfiguration(configuration);
            unmatched = patternsUnmatched;
        }
        var document = action.Activate
            ? application.OpenAndActivateDocument(openPath, options, false).Document
            : application.Application.OpenDocumentFile(openPath, options);
        if (document.IsLinked)
            throw LinkedDocumentError(path, FindLinkHost(application, document.PathName) ?? "an open document");
        capture?.Invoke(document);
        Opened[document] = (action.Mode, path);
        stopwatch.Stop();
        return new ActionResultData
        {
            Title = document.Title,
            Path = document.PathName,
            IsWorkshared = document.IsWorkshared,
            IsDetached = document.IsDetached,
            IsCentral = IsCentral(document),
            OpenedAs = action.Mode,
            Active = document.Equals(application.ActiveUIDocument?.Document),
            WorksetsOpen = WorksetNames(document),
            WorksetPatternsUnmatched = unmatched,
            Warning = ignoreWorksets ? "Worksets ignored: the model is not workshared." : null,
            Audited = action.Audit,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    internal static (Document Document, ActionResultData Result) OpenForProcessing(UIApplication application, ActionJobContract action)
    {
        action.Activate = false;
        Document? document = null;
        try
        {
            var result = Open(application, action, capture: opened => document = opened);
            return (document!, result);
        }
        catch
        {
            if (document is not null && !document.Equals(application.ActiveUIDocument?.Document))
            {
                Opened.Remove(document);
                document.Close(false);
            }
            throw;
        }
    }

    internal static void CloseForProcessing(UIApplication application, Document document)
    {
        if (document.Equals(application.ActiveUIDocument?.Document))
            throw new InvalidOperationException("The active document cannot be closed.");
        Opened.Remove(document);
        if (!document.Close(false)) throw new InvalidOperationException("Revit did not close the processed model.");
    }

    internal static void SaveForProcessing(UIApplication application, Document document, string? target,
        bool compact, bool overwrite)
    {
        Save(application, document, new ActionJobContract
        {
            SaveAs = target,
            Compact = compact,
            Overwrite = overwrite
        }, requireConfirmation: false);
    }

    private static string? FindLinkHost(UIApplication application, string path)
    {
        foreach (var host in application.Application.Documents.Cast<Document>().Where(item => !item.IsLinked))
        {
            using var collector = new FilteredElementCollector(host).OfClass(typeof(RevitLinkType));
            foreach (var link in collector.Cast<RevitLinkType>())
            {
                if (link.GetLinkedFileStatus() != LinkedFileStatus.Loaded) continue;
                var reference = ExternalFileUtils.GetExternalFileReference(host, link.Id);
                var linkPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
                if (DocumentPathValidator.SamePath(path, linkPath)) return host.Title;
            }
        }
        return null;
    }

    private static InvalidOperationException LinkedDocumentError(string path, string hostTitle) =>
        new($"'{Path.GetFileName(path.Replace('\\', '/'))}' is loaded as a link in '{hostTitle}'. Unload the link first or open it in another Revit session.");

    private static Document? _batchDocument;

    internal static Document BatchDocument => _batchDocument ??
        throw new InvalidOperationException("No batch document is open.");

    internal static ActionResultData BatchOpen(UIApplication application, string path)
    {
        if (_batchDocument is not null) throw new InvalidOperationException("A batch document is already open.");
        var result = Open(application, new ActionJobContract
        {
            DocumentPath = path,
            Mode = "detached",
            Worksets = "all",
            Activate = false
        }, batch: true);
        _batchDocument = application.Application.Documents.Cast<Document>()
            .Single(document => document.Title == result.Title && document.PathName == result.Path);
        return result;
    }

    internal static ActionResultData BatchClose()
    {
        var document = _batchDocument ?? throw new InvalidOperationException("No batch document is open.");
        var title = document.Title;
        var path = document.PathName;
        _batchDocument = null;
        Opened.Remove(document);
        if (!document.Close(false)) throw new InvalidOperationException("Revit did not close the batch document.");
        return new ActionResultData { Title = title, Path = path, Saved = false };
    }

    private static (WorksetConfiguration Configuration, List<string> Unmatched) WorksetConfiguration(ModelPath path, string selection, List<string>? names)
    {
        if (selection == "all") return (new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets), []);
        if (selection is "none") return (new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets), []);
        var available = WorksharingUtils.GetUserWorksetInfo(path).ToList();
        var (selected, unmatched) = OpenWorksetSelector.Select(available.Select(item => item.Name).ToList(), selection, names!);
        var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
        configuration.Open(available.Where(item => selected.Contains(item.Name)).Select(item => item.Id).ToList());
        return (configuration, unmatched);
    }

    private static ActionResultData Activate(UIApplication application, Document document)
    {
        if (document.Equals(application.ActiveUIDocument?.Document))
            return new ActionResultData { Title = document.Title, Path = document.PathName, Active = true, Changed = false };
        var path = document.PathName;
        var title = document.Title;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Cannot activate '{title}': it has no saved path. Save it first or open it with activate=true.");
        if (!DocumentIdentityMatcher.Contains(application.Application.Documents.Cast<Document>(), document, Identity))
            throw new InvalidOperationException("The document path no longer belongs to an open document.");
        var previous = application.ActiveUIDocument?.Document;
        var previousPath = previous?.PathName;
        var before = application.Application.Documents.Cast<Document>().ToList();
        var activated = application.OpenAndActivateDocument(path).Document;
        var after = application.Application.Documents.Cast<Document>().ToList();
        if (!activated.Equals(document) || after.Count != before.Count ||
            !DocumentIdentityMatcher.AllPresent(before, after, Identity) ||
            !DocumentPathValidator.SamePath(application.ActiveUIDocument?.Document.PathName, path) ||
            !document.Equals(application.ActiveUIDocument?.Document))
        {
            if (!DocumentIdentityMatcher.Contains(before, activated, Identity))
            {
                var extraTitle = activated.Title;
                if (string.IsNullOrWhiteSpace(previousPath))
                    throw new InvalidOperationException($"Revit opened another copy '{extraTitle}' and left it open because the previously active document has no saved path.");
                try
                {
                    application.OpenAndActivateDocument(previousPath);
                    if (activated.Equals(application.ActiveUIDocument?.Document))
                        throw new InvalidOperationException("The extra copy is still active.");
                    if (!activated.Close(false))
                        throw new InvalidOperationException("Revit did not close the extra copy.");
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException($"Revit opened another copy '{extraTitle}' and left it open: {exception.Message}", exception);
                }
            }
            throw new InvalidOperationException("Revit opened another copy instead of activating the requested document.");
        }
        return new ActionResultData { Title = title, Path = path, Active = true, Changed = true };
    }

    private static ActionResultData ActivateView(UIApplication application, Document document, ActionJobContract action)
    {
        var views = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
            .Where(item => !item.IsTemplate).ToList();
        var view = ViewReferenceMatcher.Find(views.Where(item => action.ViewType is null ||
                string.Equals(item.ViewType.ToString(), action.ViewType, StringComparison.OrdinalIgnoreCase)),
            action.View!, item => RevitValueReader.GetId(item.Id), item => item.Name,
            item => (item as ViewSheet)?.SheetNumber, item => item.ViewType.ToString());
        if (view is null) throw new InvalidOperationException($"View '{action.View}' was not found.");
        if (!document.Equals(application.ActiveUIDocument?.Document))
        {
            if (!action.ActivateDocument) throw new InvalidOperationException("The target document is not active; set activate_document=true.");
            Activate(application, document);
        }
        var uiDocument = application.ActiveUIDocument!;
        var wasOpen = uiDocument.GetOpenUIViews().Any(item => item.ViewId == view.Id);
        var wasActive = uiDocument.ActiveView.Id == view.Id;
        uiDocument.ActiveView = view;
        return new ActionResultData
        {
            Title = document.Title,
            Path = document.PathName,
            Active = true,
            View = new RevitModelMcp.Core.Models.NwcViewResult { Name = view.Name, Id = RevitValueReader.GetId(view.Id) },
            ViewOpened = !wasOpen,
            Changed = !wasActive
        };
    }

    private static ActionResultData CloseViews(UIApplication application, Document document, ActionJobContract action)
    {
        var uiDocument = application.ActiveUIDocument;
        if (!document.Equals(uiDocument?.Document)) throw new InvalidOperationException("The target document is not active.");
        var activeId = uiDocument!.ActiveView.Id;
        var open = uiDocument.GetOpenUIViews().ToList();
        var requested = action.Views is null ? open.Where(item => item.ViewId != activeId).ToList() :
            action.Views.Select(name => open.SingleOrDefault(item =>
                ReadCommandReader.FindView(document, name)?.Id == item.ViewId)
                ?? throw new InvalidOperationException($"View '{name}' is not open.")).Distinct().ToList();
        var closed = new List<string>();
        var refused = new List<string>();
        foreach (var item in requested)
        {
            var name = (document.GetElement(item.ViewId) as View)?.Name ?? item.ViewId.ToString();
            if (item.ViewId == activeId && action.KeepActive || open.Count - closed.Count <= 1)
            {
                refused.Add(name);
                continue;
            }
            try
            {
                item.Close();
                closed.Add(name);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) when (item.ViewId == activeId)
            {
                refused.Add(name);
            }
        }
        return new ActionResultData { Title = document.Title, ClosedViews = closed, RefusedViews = refused, Changed = closed.Count > 0 };
    }

    private static ActionResultData New(UIApplication application, ActionJobContract action)
    {
        if (action.SaveAs is not null && File.Exists(action.SaveAs)) throw new IOException("save_as target exists.");
        var before = application.Application.Documents.Cast<Document>().ToList();
        var template = action.Template ?? application.Application.DefaultProjectTemplate;
        var created = action.Kind == "family" ? application.Application.NewFamilyDocument(template) :
            application.Application.NewProjectDocument(template);
        var path = action.SaveAs;
        if (path is not null || action.Activate)
        {
            if (path is null)
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "new");
                Directory.CreateDirectory(directory);
                var name = action.NewDocumentName ?? $"{(action.Kind == "family" ? "Family" : "Project")} {DateTime.Now:yyyyMMdd-HHmmss}";
                var extension = action.Kind == "family" ? ".rfa" : ".rvt";
                path = Path.Combine(directory, name + extension);
                for (var suffix = 2; File.Exists(path); suffix++)
                    path = Path.Combine(directory, $"{name} {suffix}{extension}");
            }
            created.SaveAs(path);
        }
        var document = created;
        if (action.Activate)
        {
            var active = application.OpenAndActivateDocument(path!).Document;
            if (!active.Equals(created) && !created.Close(false))
                throw new InvalidOperationException($"Revit did not close the new document's background copy. Open documents: {OpenDocumentNames(application)}.");
            document = active;
        }
        var after = application.Application.Documents.Cast<Document>().ToList();
        if (!DocumentIdentityMatcher.AllPresent(before, after, Identity) ||
            path is not null && after.Count(item => DocumentPathValidator.SamePath(item.PathName, path)) != 1 ||
            action.Activate && !document.Equals(application.ActiveUIDocument?.Document))
            throw new InvalidOperationException($"New document verification failed. Open documents: {OpenDocumentNames(application)}.");
        return new ActionResultData { Title = document.Title, Path = document.PathName, Active = action.Activate, Saved = path is not null };
    }

    private static string OpenDocumentNames(UIApplication application) =>
        string.Join("; ", application.Application.Documents.Cast<Document>().Select(item => item.Title));

    private static List<string> WorksetNames(Document document) => document.IsWorkshared
        ? new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset)
            .Where(workset => workset.IsOpen).Select(workset => workset.Name).ToList()
        : [];

    private static ActionResultData Close(UIApplication application, Document document, ActionJobContract action)
    {
        if (document.Equals(application.ActiveUIDocument?.Document))
            throw new InvalidOperationException("Cannot close the active document; activate another document first.");
        if (action.Save && (IsCentral(document) || document.IsDetached && string.IsNullOrWhiteSpace(document.PathName)))
            throw new InvalidOperationException("Save the detached document under a new path before closing with save=true; an open central model cannot be saved.");
        var exempt = !action.Save && document.IsDetached && Opened.ContainsKey(document) && !IsCentral(document);
        if (action.Save || document.IsModified && !exempt || action.ConfirmToken is not null)
        {
            var operation = action.Save ? "Save and close" : "Close without saving and discard changes in";
            var confirmation = Confirm("close-document", document, action, $"{operation} '{document.Title}' ({document.PathName}).");
            if (confirmation is not null) return confirmation;
        }
        // Capture everything needed for the result, and stop touching the managed wrapper, before
        // Close() invalidates it; Revit throws when any later call (including a dictionary lookup
        // that hashes the Document) reaches an already-closed document.
        var title = document.Title;
        var path = document.PathName;
        Opened.Remove(document);
        if (!document.Close(action.Save))
            throw new InvalidOperationException($"Revit did not close '{title}'.");
        return new ActionResultData { Title = title, Path = path, Saved = action.Save };
    }

    private static ActionResultData Save(UIApplication application, Document document, ActionJobContract action,
        bool requireConfirmation = true)
    {
        if (action.SaveAs is not null)
        {
            DocumentPathValidator.Validate(action.SaveAs);
            var knownCentral = CentralPath(document);
            DocumentPathValidator.EnsureSaveAsDiffersFromCentral(action.SaveAs, knownCentral);
            if (Opened.TryGetValue(document, out var opened) && DocumentPathValidator.SamePath(action.SaveAs, opened.OriginalPath) ||
                application.Application.Documents.Cast<Document>().Any(item => DocumentPathValidator.SamePath(action.SaveAs, CentralPath(item))))
                throw new InvalidOperationException("save_as cannot overwrite a known central path.");
            if (File.Exists(action.SaveAs))
            {
                if (!action.Overwrite)
                    throw new IOException("save_as target exists; set overwrite=true to replace it.");
                bool isCentral;
                bool isWorkshared;
                try
                {
                    using var targetInfo = BasicFileInfo.Extract(action.SaveAs);
                    isCentral = targetInfo.IsCentral;
                    isWorkshared = targetInfo.IsWorkshared;
                }
                catch (Exception)
                {
                    throw new InvalidOperationException("save_as target cannot be inspected; overwrite is refused.");
                }
                DocumentPathValidator.EnsureSafeOverwrite(isCentral, isWorkshared);
            }
        }
        else if (IsCentral(document))
            throw new InvalidOperationException("Saving an open central model is refused.");
        var description = action.SaveAs is null ? $"Save '{document.Title}' to {document.PathName}, compact={action.Compact}." :
            $"Save '{document.Title}' as {action.SaveAs}, overwrite={action.Overwrite}, compact={action.Compact}.";
        if (requireConfirmation)
        {
            var confirmation = Confirm("save-document", document, action, description);
            if (confirmation is not null) return confirmation;
        }
        if (action.SaveAs is null)
        {
            var options = new SaveOptions { Compact = action.Compact };
            document.Save(options);
        }
        else
        {
            var options = new SaveAsOptions { OverwriteExistingFile = action.Overwrite, Compact = action.Compact };
            if (document.IsWorkshared && document.IsDetached)
                options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
            document.SaveAs(action.SaveAs, options);
        }
        return new ActionResultData { Title = document.Title, Path = document.PathName, Saved = true };
    }

    private static ActionResultData Sync(Document document, ActionJobContract action)
    {
        if (document.IsFamilyDocument || !document.IsWorkshared || document.IsDetached || string.IsNullOrWhiteSpace(CentralPath(document)))
            throw new InvalidOperationException("This document has no central model to synchronize.");
        if (IsCentral(document)) throw new InvalidOperationException("Synchronizing an open central model is refused.");
        var central = CentralPath(document);
        var relinquish = action.Relinquish == "custom"
            ? string.Join(", ", action.RelinquishFlags!.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))
            : action.Relinquish;
        var description = $"Synchronize '{document.Title}' with central {central}, comment '{action.Comment}', relinquish {relinquish}, compact={action.Compact}, save local before={action.SaveLocalBefore}, after={action.SaveLocalAfter}.";
        var confirmation = Confirm("sync-document", document, action, description);
        if (confirmation is not null) return confirmation;
        using var transactionOptions = new TransactWithCentralOptions();
        transactionOptions.SetLockCallback(new NoWaitForCentralLock());
        var syncOptions = new SynchronizeWithCentralOptions
        {
            Comment = action.Comment,
            Compact = action.Compact,
            SaveLocalBefore = action.SaveLocalBefore,
            SaveLocalAfter = action.SaveLocalAfter
        };
        syncOptions.SetRelinquishOptions(RelinquishOptions(action.Relinquish ?? "all", action.RelinquishFlags));
        try { document.SynchronizeWithCentral(transactionOptions, syncOptions); }
        catch (Autodesk.Revit.Exceptions.CentralModelContentionException)
        { throw new InvalidOperationException("The central model is locked."); }
        return new ActionResultData { Title = document.Title, CentralPath = central, Saved = true };
    }

    private static RelinquishOptions RelinquishOptions(string selection, Dictionary<string, bool>? values)
    {
        if (selection == "all") return new RelinquishOptions(true);
        if (selection is "none") return new RelinquishOptions(false);
        bool Enabled(string key) => values!.TryGetValue(key, out var value) && value;
        return new RelinquishOptions(false)
        {
            CheckedOutElements = Enabled("borrowed"),
            UserWorksets = Enabled("user_worksets"),
            FamilyWorksets = Enabled("family_worksets"),
            ViewWorksets = Enabled("view_worksets"),
            StandardWorksets = Enabled("standard_worksets")
        };
    }

    private static string? CentralPath(Document document)
    {
        if (!document.IsWorkshared || document.IsDetached) return null;
        var path = document.GetWorksharingCentralModelPath();
        return path is null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
    }

    private static bool IsCentral(Document document) =>
        DocumentPathValidator.SamePath(document.PathName, CentralPath(document));

    private static ActionResultData? Confirm(string command, Document document, ActionJobContract action, string description)
    {
        var identity = DocumentConfirmationBinding.Identity(document.PathName, document.Title);
        var arguments = DocumentConfirmationBinding.Arguments(action, document.PathName, document.IsModified);
        var state = ConfirmationStore.State(document);
        if (action.ConfirmToken is null)
            return new ActionResultData
            {
                NeedsConfirmation = true,
                ConfirmationText = description,
                ConfirmToken = ConfirmationStore.Tokens.Issue(command, identity, arguments, state)
            };
        var result = ConfirmationStore.Tokens.Consume(action.ConfirmToken, command, identity, arguments, state);
        if (result == DocumentConfirmationResult.DocumentChanged)
            throw new InvalidOperationException("The document changed after the preview; request a new confirmation.");
        if (result != DocumentConfirmationResult.Valid)
            throw new InvalidOperationException("Confirmation token is invalid, expired or does not match the arguments.");
        return null;
    }

    private sealed class NoWaitForCentralLock : ICentralLockedCallback
    {
        public bool ShouldWaitForLockAvailability() => false;
    }
}

[DataContract]
internal sealed class DocumentState
{
    [DataMember(Name = "title")]
    public string Title { get; set; } = "";
    [DataMember(Name = "path")]
    public string Path { get; set; } = "";
    [DataMember(Name = "isActive")]
    public bool IsActive { get; set; }
    [DataMember(Name = "isLinked")]
    public bool IsLinked { get; set; }
    [DataMember(Name = "isFamilyDocument")]
    public bool IsFamilyDocument { get; set; }
    [DataMember(Name = "isWorkshared")]
    public bool IsWorkshared { get; set; }
    [DataMember(Name = "isDetached")]
    public bool IsDetached { get; set; }
    [DataMember(Name = "isModified")]
    public bool IsModified { get; set; }
    [DataMember(Name = "openedByMcp")]
    public bool OpenedByMcp { get; set; }
    [DataMember(Name = "centralPath", EmitDefaultValue = false)]
    public string? CentralPath { get; set; }
}

[DataContract]
internal sealed class UiSessionState
{
    [DataMember(Name = "activeDocument")] public UiDocumentState? ActiveDocument { get; set; }
    [DataMember(Name = "activeView")] public UiViewState? ActiveView { get; set; }
    [DataMember(Name = "openViews")] public List<UiViewState> OpenViews { get; set; } = [];
    [DataMember(Name = "selection")] public UiSelectionState Selection { get; set; } = new();
    [DataMember(Name = "documents")] public List<DocumentState> Documents { get; set; } = [];
}

[DataContract]
internal sealed class UiDocumentState
{
    [DataMember(Name = "title")] public string Title { get; set; } = "";
    [DataMember(Name = "path")] public string Path { get; set; } = "";
    [DataMember(Name = "isFamilyDocument")] public bool IsFamilyDocument { get; set; }
    [DataMember(Name = "isWorkshared")] public bool IsWorkshared { get; set; }
    [DataMember(Name = "isModified")] public bool IsModified { get; set; }
}

[DataContract]
internal sealed class UiViewState
{
    [DataMember(Name = "id")] public long Id { get; set; }
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    [DataMember(Name = "type")] public string Type { get; set; } = "";
    [DataMember(Name = "isActive")] public bool IsActive { get; set; }
}

[DataContract]
internal sealed class UiSelectionState
{
    [DataMember(Name = "count")] public int Count { get; set; }
    [DataMember(Name = "elements")] public List<UiSelectedElement> Elements { get; set; } = [];
}

[DataContract]
internal sealed class UiSelectedElement
{
    [DataMember(Name = "id")] public long Id { get; set; }
    [DataMember(Name = "category")] public string? Category { get; set; }
    [DataMember(Name = "name")] public string Name { get; set; } = "";
}
