using System.Text;

namespace Installer;

/// <summary>
///     Script text the MultiUser installer runs for the maintenance and uninstall paths of the policy values and of
///     %ProgramData%\RevitModelMcp. It has no WixSharp dependency so the Core tests can compile and check it.
/// </summary>
static class InstallerScripts
{
    /// <summary>PowerShell exit codes of <see cref="CheckProgramDataFolderScript"/>.</summary>
    public const int FolderOk = 0, FolderIsLink = 20, FolderWrongOwner = 21;

    const string UncheckedPrefix = @"%ProgramData%\RevitModelMcp could not be checked (PowerShell exit code ";
    const string UncheckedSuffix = "). Make sure the folder is readable by Administrators and run the installer again.";

    /// <summary>
    ///     Removes the ReadOnly value the installer wrote (policy key and its own marker). It runs only when
    ///     READONLY=0 is given on a maintenance run and the marker says the installer owns the value, so a value that
    ///     Group Policy or Intune set is never touched. An upgrade drops the value with the old component instead.
    /// </summary>
    public const string ClearMsiReadOnlyScript =
        "$ErrorActionPreference = 'Stop'; " +
        "foreach ($key in 'HKLM:\\Software\\Policies\\RevitModelMcp', 'HKLM:\\Software\\RevitModelMcp\\MsiPolicy') { " +
        "if (Test-Path -LiteralPath $key) { Remove-ItemProperty -LiteralPath $key -Name 'ReadOnly' -ErrorAction SilentlyContinue } }; ";

    /// <summary>The MSI condition of the ReadOnly clearing action: maintenance or repair, READONLY=0, value owned by the installer.</summary>
    public const string ClearMsiReadOnlyCondition =
        "READONLY=\"0\" AND RMM_REMEMBERED_READONLY<>\"\" AND Installed AND NOT REMOVE~=\"ALL\"";

    /// <summary>
    ///     Check only, no change: the same test the elevated ensure script applies, reported as an exit code so the
    ///     installer can say why it refuses. A good or missing folder exits 0; any other failure exits 22.
    /// </summary>
    public const string CheckProgramDataFolderScript =
        "$ErrorActionPreference = 'Stop'; " +
        "try { " +
        "$directory = Join-Path $env:ProgramData 'RevitModelMcp'; " +
        "$admins = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'; " +
        "$system = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18'; " +
        "if (-not (Test-Path -LiteralPath $directory)) { exit 0 }; " +
        "$item = Get-Item -LiteralPath $directory -Force; " +
        "if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { exit 20 }; " +
        "$owner = (Get-Acl -LiteralPath $directory).GetOwner([System.Security.Principal.SecurityIdentifier]); " +
        "if ($owner.Value -ne $admins.Value -and $owner.Value -ne $system.Value) { exit 21 }; " +
        "exit 0 " +
        "} catch { exit 22 }";

    /// <summary>
    ///     Uninstall cleanup: deletes settings.json only when it holds nothing but the keys the installer writes
    ///     (updateCheck, updateMode), then deletes the folder only when it is empty. policy.json, logs or any other
    ///     file keeps the folder. Never recursive, never through a link.
    /// </summary>
    public const string RemoveProgramDataFolderScript =
        "$ErrorActionPreference = 'SilentlyContinue'; " +
        "$directory = Join-Path $env:ProgramData 'RevitModelMcp'; " +
        "$item = Get-Item -LiteralPath $directory -Force; " +
        "if ($item -and $item.PSIsContainer -and -not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { " +
        "$path = Join-Path $directory 'settings.json'; " +
        "if (Test-Path -LiteralPath $path -PathType Leaf) { " +
        "$settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; " +
        "if ($settings) { $foreign = @($settings.PSObject.Properties.Name | Where-Object { $_ -notin 'updateCheck', 'updateMode' }); " +
        "if ($foreign.Count -eq 0) { Remove-Item -LiteralPath $path -Force } } }; " +
        "if (-not @(Get-ChildItem -LiteralPath $directory -Force).Count) { [System.IO.Directory]::Delete($directory) } " +
        "}; exit 0";

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>The reason shown for a folder check exit code, or null when the folder is fine.</summary>
    public static string? FolderRefusal(int exitCode) => exitCode switch
    {
        FolderOk => null,
        FolderIsLink =>
            @"%ProgramData%\RevitModelMcp is a link or not a folder, so the installer will not write policy and settings into it. " +
            @"Delete it (rd /s /q ""%ProgramData%\RevitModelMcp"") and run the installer again.",
        FolderWrongOwner =>
            @"%ProgramData%\RevitModelMcp is owned by an account other than Administrators or SYSTEM, so the installer will not write policy and settings into it. " +
            @"Run this in an elevated prompt: takeown /a /f ""%ProgramData%\RevitModelMcp"" (or delete the folder), then run the installer again.",
        _ => UncheckedPrefix + exitCode + UncheckedSuffix
    };

    /// <summary>
    ///     Immediate VBScript custom action: runs the check script hidden, and on a refusal writes the reason to the
    ///     MSI log (Session.Log), shows it as an installer error message and fails the install with the text in the
    ///     script error, which the verbose log prints. It runs before InstallInitialize, so nothing is rolled back.
    /// </summary>
    public static string CheckProgramDataFolderVbScript()
    {
        var command = $"-NoProfile -NonInteractive -EncodedCommand {Encode(CheckProgramDataFolderScript)}";
        var builder = new StringBuilder();
        builder.AppendLine("Dim shell, powershell, exitCode, reason, record");
        builder.AppendLine("Set shell = CreateObject(\"WScript.Shell\")");
        builder.AppendLine("powershell = shell.ExpandEnvironmentStrings(\"%SystemRoot%\") & \"\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\"");
        builder.AppendLine($"exitCode = shell.Run(Chr(34) & powershell & Chr(34) & \" {command}\", 0, True)");
        builder.AppendLine("reason = \"\"");
        builder.AppendLine($"If exitCode = {FolderIsLink} Then reason = \"{VbString(FolderRefusal(FolderIsLink)!)}\"");
        builder.AppendLine($"If exitCode = {FolderWrongOwner} Then reason = \"{VbString(FolderRefusal(FolderWrongOwner)!)}\"");
        builder.AppendLine($"If exitCode <> {FolderOk} And exitCode <> {FolderIsLink} And exitCode <> {FolderWrongOwner} Then reason = \"{VbString(UncheckedPrefix)}\" & exitCode & \"{VbString(UncheckedSuffix)}\"");
        builder.AppendLine("If reason <> \"\" Then");
        builder.AppendLine("    Session.Log \"Model MCP: \" & reason");
        builder.AppendLine("    Set record = Session.Installer.CreateRecord(0)");
        builder.AppendLine("    record.StringData(0) = reason");
        builder.AppendLine("    Session.Message &H01000000, record");
        builder.AppendLine("    Err.Raise vbObjectError + 1, \"ModelMcpProgramDataCheck\", reason");
        builder.AppendLine("End If");
        return builder.ToString();
    }

    static string VbString(string text) => text.Replace("\"", "\"\"");
}
