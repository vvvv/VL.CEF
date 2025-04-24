using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[GitHubActions(
    "main", 
    GitHubActionsImage.WindowsLatest,
    OnPushBranches = ["master"],
    Lfs = true,
    Submodules = GitHubActionsSubmodules.Recursive,
    InvokedTargets = [nameof(Deploy)],
    ImportSecrets = [nameof(NuGetApiKey)])]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Pack);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter][Secret] 
    readonly string NuGetApiKey;

    AbsolutePath PackagesDirectory => RootDirectory / "bin";

    Target Clean => _ => _
        .Executes(() =>
        {
            PackagesDirectory.CreateOrCleanDirectory();
        });

    Target Pack => _ => _
        .DependsOn(Clean)
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() =>
        {
            DotNetPack(_ => _.SetConfiguration(Configuration));
        });

    Target Deploy => _ => _
        .DependsOn(Pack)
        .Requires(() => NuGetApiKey)
        .Executes(() =>
        {
            foreach (var file in PackagesDirectory.GetFiles("*.nupkg"))
            {
                DotNetNuGetPush(_ => _
                    .SetTargetPath(file)
                    .SetApiKey(NuGetApiKey)
                    .SetSource("nuget.org"));
            }
        });

}
