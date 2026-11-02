using System.Security.Principal;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Activity;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Output;

namespace RevitModelMcp;

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class RepairChannelPermissionsCommand : ExternalCommand
{
    public override void Execute()
    {
        if (!ChannelDirectory.AclRepairAvailable) return;
        var manualCommands = ChannelAclRepairPolicy.ManualCommands(ChannelDirectory.RootDirectory);
        try
        {
            var targets = ChannelDirectory.InspectRepair();
            var root = targets[0];
            var removed = targets.SelectMany(target => target.Plan.RemovedSids).Distinct(StringComparer.OrdinalIgnoreCase);
            manualCommands = ChannelAclRepairPolicy.ManualCommands(ChannelDirectory.RootDirectory, removed);
            var changedPaths = targets.Where(target => target.Plan.Required).Select(target =>
                target.Path == root.Path ? "." : target.Path[(root.Path.Length + 1)..]);
            var details = root.Plan.Confirmation(root.Path, PrincipalName(root.Plan.OwnerSid),
                removed.Select(PrincipalName), changedPaths);
            if (!AppDialog.Show("Repair channel permissions",
                    "Replace channel folder permissions?\n\n" + details,
                    confirm: true)) return;
            PluginLog.Info("Channel permission repair confirmed from the ribbon.");
            ActivityRecorder.RecordSystemNotice("Channel permission repair confirmed from the ribbon.");
            var ownerChangeDenied = ChannelDirectory.Repair(targets);
            var enabled = RevitModelMcp.Application.RetryFileChannel();
            var message = enabled ? "Channel permissions repaired. The file channel and heartbeat are running."
                : "Channel permissions were changed, but the file channel is still refused. Inspect the latest add-in log. " + ChannelAclRepairPolicy.ManualInstructions;
            if (ownerChangeDenied)
                message += " Windows refused an owner change (WRITE_OWNER). The existing owner remains subject to the channel ACL check. " + ChannelAclRepairPolicy.ManualInstructions;
            PluginLog.Info(message);
            ActivityRecorder.RecordSystemNotice(message, failed: !enabled);
            AppDialog.Show("Repair channel permissions", message + (enabled && !ownerChangeDenied ? string.Empty
                : "\n\n" + manualCommands));
        }
        catch (Exception exception)
        {
            PluginLog.Error("Channel permission repair failed or was incomplete.", exception);
            var message = ChannelAclRepairPolicy.ManualInstructions;
            ActivityRecorder.RecordSystemNotice(message, failed: true);
            AppDialog.Show("Repair channel permissions", message + "\n\n" +
                manualCommands);
        }
    }

    private static string PrincipalName(string sid)
    {
        try
        {
            var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            return $"{account} ({sid})";
        }
        catch (IdentityNotMappedException) { return sid; }
        catch (ArgumentException) { return sid; }
    }
}

[UsedImplicitly]
public sealed class RepairChannelPermissionsAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
        ChannelDirectory.AclRepairAvailable;
}
