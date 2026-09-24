using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class DocumentActions
{
    private static readonly Dictionary<Document, (string Mode, string OriginalPath)> Opened = [];

    internal static ActionResultData Execute(UIApplication application, string command, ActionJobContract action)
    {
        if (command == "open-document") return Open(application, action);
        var document = ActionCommandExecutor.ResolveDocument(application, action.Document);
        if (document.IsModifiable) throw new InvalidOperationException("The document has an open transaction.");
        return command switch
        {
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

    private static ActionResultData Open(UIApplication application, ActionJobContract action)
    {
        var stopwatch = Stopwatch.StartNew();
        var path = action.DocumentPath!;
        var server = path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase);
        if (server && !ModelPathUtils.IsValidUserVisibleFullServerPath(path))
            throw new ArgumentException("Invalid Revit Server model path.");
        var sourcePath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        var isCentral = server || BasicFileInfo.Extract(path).IsCentral;
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
            Audit = false,
            DetachFromCentralOption = isCentral && action.Mode.StartsWith("detached", StringComparison.Ordinal)
                ? action.Mode == "detached_discard_worksets"
                    ? DetachFromCentralOption.DetachAndDiscardWorksets
                    : DetachFromCentralOption.DetachAndPreserveWorksets
                : DetachFromCentralOption.DoNotDetach
        };
        options.SetOpenWorksetsConfiguration(WorksetConfiguration(openPath, action.Worksets, action.WorksetsOpenNames));
        var document = action.Activate
            ? application.OpenAndActivateDocument(openPath, options, false).Document
            : application.Application.OpenDocumentFile(openPath, options);
        Opened[document] = (action.Mode, path);
        stopwatch.Stop();
        return new ActionResultData
        {
            Title = document.Title, Path = document.PathName, IsWorkshared = document.IsWorkshared,
            IsDetached = document.IsDetached, IsCentral = IsCentral(document), OpenedAs = action.Mode,
            Active = ReferenceEquals(application.ActiveUIDocument?.Document, document),
            WorksetsOpen = WorksetNames(document), ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static WorksetConfiguration WorksetConfiguration(ModelPath path, string selection, List<string>? openNames)
    {
        if (selection == "all") return new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
        if (selection is "none") return new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
        var names = new HashSet<string>(openNames!, StringComparer.OrdinalIgnoreCase);
        var available = WorksharingUtils.GetUserWorksetInfo(path).ToList();
        if (names.Except(available.Select(item => item.Name), StringComparer.OrdinalIgnoreCase).Any())
            throw new ArgumentException("A requested workset was not found.");
        var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
        configuration.Open(available.Where(item => names.Contains(item.Name)).Select(item => item.Id).ToList());
        return configuration;
    }

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
        if (action.Save || document.IsModified && !exempt)
        {
            var operation = action.Save ? "Save and close" : "Close without saving and discard changes in";
            var confirmation = Confirm("close-document", document, action, $"{operation} '{document.Title}' ({document.PathName}).");
            if (confirmation is not null) return confirmation;
        }
        else if (action.ConfirmToken is not null)
            throw new InvalidOperationException("Confirmation token does not match the current document state.");
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

    private static ActionResultData Save(UIApplication application, Document document, ActionJobContract action)
    {
        if (action.SaveAs is not null)
        {
            DocumentPathValidator.Validate(action.SaveAs);
            var knownCentral = CentralPath(document);
            if (DocumentPathValidator.SamePath(action.SaveAs, knownCentral) ||
                Opened.TryGetValue(document, out var opened) && DocumentPathValidator.SamePath(action.SaveAs, opened.OriginalPath) ||
                application.Application.Documents.Cast<Document>().Any(item => DocumentPathValidator.SamePath(action.SaveAs, CentralPath(item))))
                throw new InvalidOperationException("save_as cannot overwrite a known central path.");
            if (File.Exists(action.SaveAs) && !action.Overwrite)
                throw new IOException("save_as target exists; set overwrite=true to replace it.");
        }
        else if (IsCentral(document))
            throw new InvalidOperationException("Saving an open central model is refused.");
        var description = action.SaveAs is null ? $"Save '{document.Title}' to {document.PathName}, compact={action.Compact}." :
            $"Save '{document.Title}' as {action.SaveAs}, overwrite={action.Overwrite}, compact={action.Compact}.";
        var confirmation = Confirm("save-document", document, action, description);
        if (confirmation is not null) return confirmation;
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
            Comment = action.Comment, Compact = action.Compact,
            SaveLocalBefore = action.SaveLocalBefore, SaveLocalAfter = action.SaveLocalAfter
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
            CheckedOutElements = Enabled("borrowed"), UserWorksets = Enabled("user_worksets"),
            FamilyWorksets = Enabled("family_worksets"), ViewWorksets = Enabled("view_worksets"),
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
        if (action.ConfirmToken is null)
            return new ActionResultData { NeedsConfirmation = true, ConfirmationText = description,
                ConfirmToken = ConfirmationStore.Tokens.Issue(command, identity, arguments) };
        if (!ConfirmationStore.Tokens.Consume(action.ConfirmToken, command, identity, arguments))
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
