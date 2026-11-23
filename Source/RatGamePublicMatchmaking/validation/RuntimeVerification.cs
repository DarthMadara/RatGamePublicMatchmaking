using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class RuntimeVerification
{
    internal static void Run(string root, string game)
    {
        root = Path.GetFullPath(root); game = Path.GetFullPath(game);
        string inspection = Path.Combine(root, "inspection");
        using var validation = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts", "Validation.json")));
        var manifest = validation.RootElement;
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string installedHash = Hash(Path.Combine(game, "BepInEx", "plugins", "RatGamePublicMatchmaking", "RatGamePublicMatchmaking.dll"));
        if (installedHash != manifest.GetProperty("PluginSHA256").GetString()) throw new Exception("Installed DLL does not match validated build.");
        foreach (var original in manifest.GetProperty("GameAssembliesUnchanged").EnumerateObject())
            if (Hash(Path.Combine(game, "Rat Game_Data", "Managed", original.Name)) != original.Value.GetString())
                throw new Exception("Original assembly changed: " + original.Name);
        string previous = Hash(Path.Combine(root, "rollback", "RatGamePublicMatchmaking.native-ui.1.dll"));
        if (previous != "BEC76D34A8564B5298ED7B9DA2CF3628C0139DA0C95F79C72ECF2A194CF2F07A") throw new Exception("Rollback DLL changed.");
        string steam = File.ReadAllText(Path.Combine(inspection, "FinalTest.Steam.BepInEx.log"));
        string direct = File.ReadAllText(Path.Combine(inspection, "FinalTest.Direct.BepInEx.log"));
        string ui = File.ReadAllText(Path.Combine(inspection, "UiState.log"));
        string config = File.ReadAllText(Path.Combine(game, "BepInEx", "config", "local.ratgame.publicmatchmaking.cfg"));
        var checks = new Dictionary<string, bool>
        {
            ["SteamBuildReady"] = steam.Contains("clientReady=True; appsInterfaceReady=True; build=26403247; reason=ready"),
            ["WaitingHostAdvertised"] = steam.Contains("marker=1; state=waiting; publicJoinable=1; phase=Waiting; lobbyLoadConfirmed=True"),
            ["OwnWaitingHostReturnedByFilteredSteamQuery"] = steam.Contains("containsCurrentHost=True"),
            ["StartWithdrawn"] = steam.Contains("marker=0; state=starting; publicJoinable=0; phase=Starting; lobbyLoadConfirmed=False"),
            ["ActiveGameWithdrawn"] = steam.Contains("marker=0; state=ingame; publicJoinable=0; phase=InGame; lobbyLoadConfirmed=False"),
            ["NativeGlobalJoinabilityRetainedDuringPlay"] = steam.Contains("lastAcceptedJoinable=True; marker=0; state=ingame"),
            ["ReturnRestoresWhenRequested"] = Regex.IsMatch(steam, @"Host lifecycle=unknown; Public Hosting requested=True.*Host lifecycle=waiting; Public Hosting requested=True.*key=ratgame.public; value=1; success=True", RegexOptions.Singleline),
            ["ReturnStaysOffWhenRequestDisabled"] = Regex.IsMatch(steam, @"state=waiting; publicJoinable=0; phase=Waiting; lobbyLoadConfirmed=True;[^\r\n]*publicRequested=False; advertised=False"),
            ["NativeVisibilityDisablesPublicIntent"] = steam.Contains("Native visibility selection=FriendsOnly; Public Hosting=False; public advertisement active=False."),
            ["DisconnectClearsOwnedHostAndResults"] = steam.Contains("currentLobby=False; hostReference=False; advertisementReference=False; publicResults=0; publicScopes=0; joinPending=False; controlledConnection=False; listening=False"),
            ["PublicTooltipPaperAndLabelLevel"] = ui.Contains("labelWorldRotation=(0.00, 0.00, 0.00) paperWorldRotation=(0.00, 0.00, 0.00)"),
            ["UnavailableSteamRejectsRefresh"] = direct.Contains("Steam is not ready. Try Refresh after Steam initializes."),
            ["PublicHostingFinallyOff"] = Regex.IsMatch(config, @"^PublicHosting = false\s*$", RegexOptions.Multiline),
            ["DiagnosticToolsFinallyOff"] = Regex.IsMatch(config, @"^NativeUiTools = false\s*$", RegexOptions.Multiline)
        };
        foreach (var check in checks) if (!check.Value) throw new Exception("Runtime evidence missing: " + check.Key);
        var bounds = Regex.Matches(ui, @"TitleBrowser bounds=\(([-\d.]+), ([-\d.]+), [^)]+\) -> \(([-\d.]+), ([-\d.]+), [^)]+\); safe=\(x:([-\d.]+), y:([-\d.]+), width:([-\d.]+), height:([-\d.]+)\)");
        if (bounds.Count == 0) throw new Exception("No captured UI bounds.");
        foreach (Match match in bounds)
        {
            var n = Enumerable.Range(1, 8).Select(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
            if (n[0] < n[4] - 1 || n[1] < n[5] - 1 || n[2] > n[4] + n[6] + 1 || n[3] > n[5] + n[7] + 1)
                throw new Exception("Browser outside captured safe area: " + match.Value);
        }
        var report = new
        {
            VerifiedUtc = DateTime.UtcNow,
            Revision = manifest.GetProperty("PluginInformationalVersion").GetString(),
            InstalledSHA256 = installedHash,
            PreviousWorkingSHA256 = previous,
            OriginalAssemblyHashesStillMatch = true,
            VerifiedFromRuntimeLogs = checks,
            CapturedBoundsWithinSafeArea = bounds.Count,
            NonFriendRemoteJoiningTested = false,
            PhysicalControllerTested = false,
            AtomicAdmissionProtectionClaimed = false,
            ReleasePackagedOrPublished = false,
            GameClosedAfterTests = Process.GetProcessesByName("Rat Game").Length == 0
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(inspection, "FinalState.json"), json);
        Console.WriteLine(json);
    }
}
