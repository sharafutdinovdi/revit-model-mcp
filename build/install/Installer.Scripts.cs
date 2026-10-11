using System.Text;

namespace Installer;

/// <summary>
///     Script text the MultiUser installer runs for the maintenance and uninstall paths of the policy values and of
///     %ProgramData%\RevitModelMcp. It has no WixSharp dependency so the Core tests can compile and check it.
/// </summary>
static class InstallerScripts
{
    /// <summary>PowerShell exit codes of <see cref="CheckProgramDataFolderScript"/>.</summary>
    public const int FolderOk = 0, FolderIsLink = 20, FolderWrongOwner = 21, FolderUnchecked = 22;

    const string UncheckedPrefix = @"%ProgramData%\RevitModelMcp could not be checked (";
    const string UncheckedSuffix = "). Make sure the folder is readable by Administrators and run the installer again.";

    /// <summary>
    ///     Removes the ReadOnly value the installer wrote (policy key and its own marker). It runs only when
    ///     READONLY=0 is given on a maintenance run and the marker says the installer owns the value, so a value that
    ///     Group Policy or Intune set is never touched. An upgrade drops the value with the old component instead.
    /// </summary>
    public const string ClearMsiReadOnlyScript =
        "$ErrorActionPreference = 'Stop'; " +
        "try { $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64'); " +
        "foreach ($subKey in 'Software\\Policies\\RevitModelMcp', 'Software\\RevitModelMcp\\MsiPolicy') { " +
        "$key = $hklm.OpenSubKey($subKey, $true); " +
        "if ($key) { try { $key.DeleteValue('ReadOnly', $false) } finally { $key.Dispose() } } } } " +
        "catch { Write-Output ('Model MCP: could not clear the ReadOnly policy value: ' + $_.Exception.Message); exit 1 }; ";

    /// <summary>The MSI condition of the ReadOnly clearing action: maintenance or repair, READONLY=0, value owned by the installer.</summary>
    public const string ClearMsiReadOnlyCondition =
        "READONLY=\"0\" AND RMM_REMEMBERED_READONLY<>\"\" AND Installed AND NOT REMOVE~=\"ALL\"";

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
        _ => UncheckedPrefix + "error " + exitCode + UncheckedSuffix
    };

    /// <summary>
    ///     Check only, no change: the same test the elevated ensure script applies. A good or missing folder exits 0.
    ///     A refusal prints "Model MCP: &lt;reason&gt;" and exits with its own code (20 link, 21 wrong owner, 22 not checkable),
    ///     so WixQuietExec copies the sentence to the MSI log. With <paramref name="showMessage"/> the same text also goes
    ///     to a message box, for the interactive install that runs in the user's session.
    /// </summary>
    public static string CheckProgramDataFolderScript(bool showMessage)
    {
        static string Quoted(string text) => "'" + text.Replace("'", "''") + "'";
        string Refuse(string reasonExpression, int exitCode) =>
            $"$reason = {reasonExpression}; Write-Output ('Model MCP: ' + $reason); " +
            (showMessage
                ? "Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show($reason, 'Model MCP installer', 'OK', 'Error'); "
                : string.Empty) +
            $"exit {exitCode}";
        return
            "$ErrorActionPreference = 'Stop'; " +
            "try { " +
            "$directory = Join-Path $env:ProgramData 'RevitModelMcp'; " +
            "$admins = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'; " +
            "$system = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18'; " +
            "if (-not (Test-Path -LiteralPath $directory)) { exit 0 }; " +
            "$item = Get-Item -LiteralPath $directory -Force; " +
            "if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { " +
            Refuse(Quoted(FolderRefusal(FolderIsLink)!), FolderIsLink) + " }; " +
            "$owner = (Get-Acl -LiteralPath $directory).GetOwner([System.Security.Principal.SecurityIdentifier]); " +
            "if ($owner.Value -ne $admins.Value -and $owner.Value -ne $system.Value) { " +
            Refuse(Quoted(FolderRefusal(FolderWrongOwner)!), FolderWrongOwner) + " }; " +
            "exit 0 " +
            "} catch { " +
            Refuse($"{Quoted(UncheckedPrefix)} + $_.Exception.GetType().Name + {Quoted(UncheckedSuffix)}", FolderUnchecked) + " }";
    }
}
