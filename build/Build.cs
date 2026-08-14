using _build;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Fallout.Common;
using Fallout.Common.Git;
using Fallout.Common.IO;
using Fallout.Solutions;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Tools.Git;
using Serilog;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

partial class Build : FalloutBuild
{
    [Solution(GenerateProjects = true)]
    readonly Solution Solution;

    [GitRepository]
    readonly GitRepository Repository;

    public static int Main() => Execute<Build>(x => x.Package);

    [Parameter("Target HollowKnight versions",
        ValueProviderType = typeof(HollowKnightVersionInfo),
        ValueProviderMember = nameof(HollowKnightVersionInfo.AllVersionStrings))]
    readonly HollowKnightVersionInfo[] TargetHollowKnightVersions;

    [Parameter("Whether to treat the build as a dryrun. Actions will be logged but not performed.")]
    readonly bool DryRun;

    [Secret, Parameter("The NuGet API key to use for publishing")]
    readonly string NuGetApiKey;

    [Secret, Parameter("Steam username for downloading game files. If either username or password is absent, QR login will be used.")]
    readonly string SteamUser;
    [Secret, Parameter("Steam password for downloading game files. If either username or password is absent, QR login will be used.")]
    readonly string SteamPassword;

    readonly AbsolutePath BinDir = RootDirectory / "bin";
    readonly AbsolutePath ObjDir = RootDirectory / "obj";
    readonly AbsolutePath DepotStagingDir = TemporaryDirectory / "depots";
    readonly AbsolutePath ModdingApiStagingDir = TemporaryDirectory / "moddingapi";

    Target Clean => _ => _
        .Before(DownloadDepots, Package, Publish)
        .Executes(() =>
        {
            BinDir.CreateOrCleanDirectory();
            ObjDir.CreateOrCleanDirectory();
            DepotStagingDir.CreateOrCleanDirectory();
        });

    Target Package => _ => _
        .Description("Builds NuGet packages for the specified game versions. If no versions are specified, all versions will be build.")
        .Executes(() =>
        {
            IEnumerable<HollowKnightVersionInfo> targetVersions = GetTargetVersions();
            Log.Information("Packaging for target versions {Versions}", string.Join(", ", targetVersions));
            if (DryRun)
            {
                Log.Information("Skipping build in dryrun mode");
                return;
            }

            DotNetTasks.DotNetPack(_ => _
                .SetProject(Solution.HollowKnight_GameLibs.RelativePath)
                .SetConfiguration("Release")
                .CombineWith(targetVersions, (_, v) => _
                    .SetProperty("BaseIntermediateOutputPath", $"{ObjDir / v}/")
                    .SetProperty("BaseOutputPath", $"{BinDir / v}/")
                    .SetProperty("TargetHollowKnightVersion", v.Version)
                    .SetProperty("TargetModdingApiVersion", v.ModdingApiVersion)),
                degreeOfParallelism: 8
            );
        });

    Target Publish => _ => _
        .Description("Publishes NuGet packages for the specified game versions with guardrails. If no versions are specified, all versions will published.")
        .DependsOn(Clean, Package)
        .Requires(() => NuGetApiKey)
        .Executes(() =>
        {
            Assert.True(GitTasks.GitHasCleanWorkingCopy(), "Error: You have uncommitted changes. Please commit or stash them before publishing.");
            Assert.True(Repository.IsOnMainBranch(), $"Error: You must be on the main branch to publish. Current branch: {Repository.Branch}");

            GitTasks.Git("fetch origin main");
            GitTasks.Git("diff --quiet HEAD origin/main", exitHandler: p =>
            {
                if (p.ExitCode != 0)
                {
                    Assert.Fail("Error: Local main branch is not up to date with origin/main. Please pull/rebase first.");
                }
                return null;
            });

            IEnumerable<HollowKnightVersionInfo> targetVersions = GetTargetVersions();
            Log.Information("On main branch, up to date with origin/main, and no uncommitted changes.");
            Log.Information("Publishing for target versions {Versions}", string.Join(", ", targetVersions));

            if (DryRun)
            {
                Log.Information("Skipping publish in dryrun mode");
                return;
            }
            IEnumerable<AbsolutePath> packages = targetVersions.Select(v => BinDir / v).SelectMany(x => x.GlobFiles("**/*.nupkg"));
            DotNetTasks.DotNetNuGetPush(_ => _
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey)
                .SetSkipDuplicate(true)
                .CombineWith(packages, (_, p) => _
                    .SetTargetPath(p)),
                degreeOfParallelism: 8
            );
        });

    Target DownloadDepots => _ => _
        .Description("Downloads game files for the specified game versions. If no versions are specified, all versions will be downloaded.")
        .Before(Package)
        .Executes(async () =>
        {
            IEnumerable<HollowKnightVersionInfo> targetVersions = GetTargetVersions();
            Log.Information("Downloading game files for target versions {Versions}", string.Join(", ", targetVersions));

            SteamClientWrapper clientWrapper = new();

            await clientWrapper.ConnectAndLoginAsync(SteamUser, SteamPassword);

            uint depotId = HollowKnightVersionInfo.STEAM_DEPOT_ID_WINDOWS;

            if (DryRun)
            {
                Log.Information("All downloads will be skipped in dryrun mode");
            }

            await Parallel.ForEachAsync(targetVersions, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (v, ct) =>
            {
                AbsolutePath targetDir = RootDirectory / "ref" / v;
                if (!IsEmptyOrAbsentDirectory(targetDir))
                {
                    Log.Warning("{TargetPath} exists and is not empty, skipping download", targetDir);
                    return;
                }

                AbsolutePath intermediateDir = DepotStagingDir / v;
                intermediateDir.CreateOrCleanDirectory();

                DepotManifest manifest = await clientWrapper.GetManifestAsync(depotId, v.WindowsManifestId);
                IEnumerable<DepotManifest.FileData> validFiles = manifest.Files
                    .Where(x =>
                        !x.Flags.HasFlag(EDepotFileFlag.Directory)
                        && !x.Flags.HasFlag(EDepotFileFlag.Symlink)
                    );
                foreach (DepotManifest.FileData file in validFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    // normalize to unix-like file names
                    file.FileName = file.FileName.Replace('\\', '/');
                    if (ManagedPathMatcher().IsMatch(file.FileName))
                    {
                        string relativePath = Path.GetRelativePath("hollow_knight_Data/Managed", Path.TrimEndingDirectorySeparator(file.FileName));
                        AbsolutePath intermediatePath = intermediateDir / relativePath;
                        Log.Information("{Version}: Downloading {File} to {Path}", v, relativePath, intermediatePath);

                        if (DryRun)
                        {
                            continue;
                        }

                        using FileStream fs = intermediatePath.ToFileInfo().OpenWrite();
                        await clientWrapper.DownloadFileAsync(depotId, file, fs, ct);
                    }
                }

                Log.Information("{Version}: Copying {Intermediate} to {Target}", v, intermediateDir, targetDir);
                intermediateDir.Copy(targetDir, ExistsPolicy.DirectoryMerge, createDirectories: true);
            });

            await clientWrapper.LogOutAsync();
        });

    Target DownloadModdingApi => _ => _
        .Description("Install the Modding API assemblies onto the downloaded game files")
        .After(DownloadDepots)
        .Before(Package)
        .Executes(async () =>
        {
            IEnumerable<HollowKnightVersionInfo> targetVersions = GetTargetVersions();
            Log.Information("Applying Modding API for target versions {Versions}", string.Join(", ", targetVersions));

            foreach (HollowKnightVersionInfo v in targetVersions)
            {
                AbsolutePath targetDir = RootDirectory / "ref" / v;
                Assert.True(targetDir.DirectoryExists(), $"{targetDir} does not exist, run DownloadDepots first");

                AbsolutePath stagingDir = ModdingApiStagingDir / v;
                stagingDir.CreateOrCleanDirectory();

                AbsolutePath sourceDir = await MaterializeModdingApiAsync(v, stagingDir);
                if (sourceDir == null)
                {
                    continue;
                }

                int replaced = 0, added = 0;
                foreach (AbsolutePath file in sourceDir.GlobFiles("**/*.dll").Concat(sourceDir.GlobFiles("**/*.xml")))
                {
                    // skip unity_scene_repacker
                    if (file.Extension == ".dll" && !IsManagedAssembly(file))
                    {
                        Log.Information("{Version}: Skipping native library {File}", v, file.Name);
                        continue;
                    }

                    AbsolutePath destination = targetDir / file.Name;
                    if (destination.FileExists())
                    {
                        replaced++;
                    }
                    else
                    {
                        added++;
                    }
                    file.Copy(destination, ExistsPolicy.FileOverwrite);
                }

                Assert.True(replaced + added > 0, $"{v}: Modding API source contained no assemblies, check {v.ModdingApi}");
                Log.Information("{Version}: replaced {Replaced} and added {Added} files from the Modding API", v, replaced, added);
            }
        });

    private static bool IsManagedAssembly(AbsolutePath path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        return reader.HasMetadata;
    }

    /// Returns the directory holding the Modding API files, or null in dryrun mode.
    private async Task<AbsolutePath> MaterializeModdingApiAsync(HollowKnightVersionInfo v, AbsolutePath stagingDir)
    {
        switch (v.ModdingApi)
        {
            case ModdingApiSource.Zip zip:
                Log.Information("{Version}: Downloading {Url}", v, zip.Url);
                if (DryRun)
                {
                    return null;
                }
                AbsolutePath archive = stagingDir / "moddingapi.zip";
                await DownloadAsync(zip.Url, archive);
                AbsolutePath extracted = stagingDir / "extracted";
                await ZipFile.ExtractToDirectoryAsync(archive, extracted);
                return extracted;

            case ModdingApiSource.Assembly assembly:
                Log.Information("{Version}: Downloading {Url}", v, assembly.Url);
                if (DryRun)
                {
                    return null;
                }
                AbsolutePath single = stagingDir / "extracted";
                single.CreateDirectory();
                await DownloadAsync(assembly.Url, single / Path.GetFileName(new Uri(assembly.Url).LocalPath));
                return single;

            default:
                throw new NotSupportedException($"Unhandled Modding API source {v.ModdingApi.GetType().Name}");
        }
    }

    // Increase timeout from 5s
    private static Task DownloadAsync(string url, AbsolutePath path) =>
        HttpTasks.HttpDownloadFileAsync(url, path, clientConfigurator: client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            return client;
        });

    Target GetCurrentVersionInfo => _ => _
        .Description("Uses Steam to get the version info (manifest info and game version) for the latest version of HollowKnight")
        .Executes(async () =>
        {
            SteamClientWrapper clientWrapper = new();
            await clientWrapper.ConnectAndLoginAsync(SteamUser, SteamPassword);

            DepotManifestIds manifestIds = await clientWrapper.GetProductInfoAsync();
            Log.Information("Got manifest details for latest version: {Info}", manifestIds);

            uint depotId = HollowKnightVersionInfo.STEAM_DEPOT_ID_WINDOWS;
            DepotManifest manifest = await clientWrapper.GetManifestAsync(depotId, manifestIds.Windows);

            DepotManifest.FileData ggm = manifest.Files.FirstOrDefault(f => f.FileName.Replace('\\', '/').Equals("hollow_knight_Data/globalgamemanagers"));
            Assert.NotNull(ggm, "Failed to find globalgamemanagers file in manifest");
            using MemoryStream ms = new();
            await clientWrapper.DownloadFileAsync(depotId, ggm, ms);
            ms.Seek(0, SeekOrigin.Begin);

            await clientWrapper.LogOutAsync();

            AssetsManager manager = new();
            // the latest LZ4 TPK can be obtained from https://github.com/AssetRipper/Tpk/blob/master/README.md
            manager.LoadClassPackage(RootDirectory / "build" / "classdata.tpk");

            AssetsFileInstance afi = manager.LoadAssetsFile(ms, "globalgamemanagers");
            manager.LoadClassDatabaseFromPackage(afi.file.Metadata.UnityVersion);
            Log.Information("Loaded globalgamemanagers from manifest (Unity version {UnityVersion})", afi.file.Metadata.UnityVersion);

            AssetFileInfo playerSettings = afi.file.GetAssetsOfType(AssetClassID.PlayerSettings).First();
            Assert.NotNull(playerSettings, "PlayerSettings not found in globalgamemanagers");

            AssetTypeValueField psBase = manager.GetBaseField(afi, playerSettings);
            string gameVersion = psBase["bundleVersion"]?.AsString;
            Assert.NotNullOrEmpty(gameVersion, "Game version not present in globalgamemanagers");

            HollowKnightVersionInfo existingRecord = HollowKnightVersionInfo.AllVersions.FirstOrDefault(v => v.Version == gameVersion);
            if (existingRecord == null)
            {
                Log.Information("Found untracked game version {Version}: {Info}", gameVersion, manifestIds);
            }
            else if (existingRecord.Manifests != manifestIds)
            {
                Log.Warning(
                    "Found conflict between tracked version {Existing} and new version {Info}",
                    existingRecord.Manifests,
                    manifestIds
                );
            }
            else
            {
                Log.Information("All up to date for latest version {Version}: {Info}", gameVersion, manifestIds);
            }
        });

    private IEnumerable<HollowKnightVersionInfo> GetTargetVersions()
    {
        return TargetHollowKnightVersions != null && TargetHollowKnightVersions.Length > 0
            ? TargetHollowKnightVersions
            : HollowKnightVersionInfo.AllVersions;
    }

    private bool IsEmptyOrAbsentDirectory(AbsolutePath path)
    {
        if (path.FileExists())
        {
            // file is not a directory!
            return false;
        }

        if (!path.DirectoryExists())
        {
            // doesn't exist
            return true;
        }

        if (path.GetFiles().Any() || path.GetDirectories().Any())
        {
            // has stuff in it
            return false;
        }

        return true;
    }

    [GeneratedRegex(@"^hollow_knight_Data/Managed/.+$")]
    private static partial Regex ManagedPathMatcher();
}
