using Installer;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.Controls;
using WixToolset.Dtf.WindowsInstaller;

const string outputName = "RevitModelMcp";
const string projectName = "RevitModelMcp";

var versioning = Versioning.CreateFromVersionString(args[0]);
var project = new Project
{
    OutDir = "output",
    Name = projectName,
    Platform = Platform.x64,
    UI = WUI.WixUI_FeatureTree,
    MajorUpgrade = MajorUpgrade.Default,
    GUID = new Guid("75e1b812-23a4-45ce-9e7c-84d7d43b8c70"),
    BannerImage = @"build\install\Resources\Icons\BannerImage.png",
    BackgroundImage = @"build\install\Resources\Icons\BackgroundImage.png",
    Version = versioning.VersionPrefix,
    ControlPanelInfo =
    {
        Manufacturer = "Dinar Sharafutdinov",
        ProductIcon = @"build\install\Resources\Icons\ShellIcon.ico"
    },
    Actions =
    [
        new PathFileAction(new Id("RegisterHttpUrlAcl"),
            @"[System64Folder]netsh.exe",
            "http add urlacl url=http://127.0.0.1:53110/ sddl=\"D:(A;;GX;;;[UserSID])\"",
            "System64Folder", Return.ignore, When.After, Step.InstallFiles,
            new Condition("NOT REMOVE~=\"ALL\""))
        {
            Execute = Execute.deferred,
            Impersonate = false
        },
        new PathFileAction(new Id("RemoveHttpUrlAcl"),
            @"[System64Folder]netsh.exe",
            "http delete urlacl url=http://127.0.0.1:53110/",
            "System64Folder", Return.ignore, When.Before, Step.RemoveFiles,
            new Condition("REMOVE=\"ALL\" AND NOT UPGRADINGPRODUCTCODE"))
        {
            Execute = Execute.deferred,
            Impersonate = false
        }
    ]
};

var wixEntities = Generator.GenerateWixEntities(args[1..]);
project.RemoveDialogsBetween(NativeDialogs.WelcomeDlg, NativeDialogs.CustomizeDlg);

BuildSingleUserMsi();
BuildMultiUserMsi();

void BuildSingleUserMsi()
{
    project.Scope = InstallScope.perUser;
    project.OutFileName = $"{outputName}-{versioning.Version}-SingleUser";
    project.Dirs =
    [
        new Dir(@"%AppDataFolder%\Autodesk\Revit\Addins\", [.. wixEntities.Select(entity => entity.Directory)])
    ];
    var installerPath = project.BuildMsi();
    // WiX perUser sets the no-elevation bit; URL ACL custom actions require elevation.
    using var database = new Database(installerPath, DatabaseOpenMode.Direct);
    database.SummaryInfo.WordCount &= ~8;
    database.Commit();
}

void BuildMultiUserMsi()
{
    project.Scope = InstallScope.perMachine;
    project.OutFileName = $"{outputName}-{versioning.Version}-MultiUser";

    project.Dirs = wixEntities
        .GroupBy(entity => entity.Version switch
        {
            >= 2027 => @"%ProgramFiles%\Autodesk\Revit\Addins",
            _ => @"%CommonAppDataFolder%\Autodesk\Revit\Addins"
        })
        .Select(root => new Dir(root.Key, [.. root.Select(entity => entity.Directory)]))
        .ToArray();

    project.BuildMsi();
}
