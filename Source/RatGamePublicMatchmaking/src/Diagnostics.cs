using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private Lobby? diagnosticLobby;
        private int hostSequence;
        private readonly HashSet<ulong> retiredHosts = new HashSet<ulong>();
        private string acceptedType = "unobserved";
        private bool? acceptedJoinable;
        private string buildState = "";
        private float nextReadinessPoll;
        private float snapshotAt = -1;
        private string snapshotReason = "";
        private float cleanupAt = -1;
        private int cleanupSequence;
        private readonly Dictionary<GameObject, bool> revealedControls = new Dictionary<GameObject, bool>();
        private static readonly System.Reflection.MethodInfo AppsGetter = AccessTools.PropertyGetter(typeof(SteamApps), "Internal");

        private bool AppsReady
        {
            get { try { return AppsGetter != null && AppsGetter.Invoke(null, null) != null; } catch { return false; } }
        }

        private void Trace(string operation, string facts) =>
            Logger.LogInfo("[diag host#" + hostSequence + "] " + operation + ": " + facts);

        private bool TryLiveBuild(out string build, string context)
        {
            bool ready = LiveBuild.TryRead(SteamClient.IsValid, () => SteamApps.BuildId, out build, out string reason);
            string state = "clientReady=" + SteamClient.IsValid + "; appsInterfaceReady=" + AppsReady +
                "; build=" + (ready ? build : "unavailable") + "; reason=" + reason;
            if (state != buildState) { buildState = state; Trace("build-readiness/" + context, state); }
            return ready;
        }

        internal void NativeBuildIdFailure(Exception? error)
        {
            if (error == null) return;
            Trace("native-StartScreen.GetBuildId-failure", "exception=" + error.GetType().Name +
                "; clientReady=" + SteamClient.IsValid + "; appsInterfaceReady=" + AppsReady +
                "; native exception preserved");
        }

        internal void BeginLobbyDiagnostics(Result result, Lobby lobby)
        {
            if (result != Result.OK || !SteamClient.IsValid || !lobby.IsOwnedBy(SteamClient.SteamId)) return;
            bool clean = advertisement == null && hostedLobby == null && controlledConnectedId == 0 && pendingJoin == null;
            hostSequence++;
            diagnosticLobby = lobby;
            acceptedType = "unobserved";
            acceptedJoinable = null;
            Trace("create-before-native", "publicRequested=" + publicHosting.Value + "; pluginSessionStateClean=" + clean +
                "; reusesRetiredLobby=" + retiredHosts.Contains(lobby.Id));
            TryLiveBuild(out _, "create");
        }

        private bool IsDiagnosticLobby(Lobby lobby) => diagnosticLobby.HasValue &&
            (ulong)diagnosticLobby.Value.Id == (ulong)lobby.Id;

        internal void RecordTypeSetter(Lobby lobby, string type, bool success)
        {
            if (!IsDiagnosticLobby(lobby)) return;
            if (success) acceptedType = type;
            Trace("Steam.SetLobbyType", "requested=" + type + "; success=" + success);
        }

        internal void RecordJoinableSetter(Lobby lobby, bool value, bool success)
        {
            if (!IsDiagnosticLobby(lobby)) return;
            if (success) acceptedJoinable = value;
            Trace("Steam.SetLobbyJoinable", "requested=" + value + "; success=" + success);
        }

        internal void RecordMetadataSetter(Lobby lobby, string key, string value, bool success)
        {
            if (!IsDiagnosticLobby(lobby)) return;
            if (key == PublicPolicy.StateKey || key == PublicPolicy.JoinableKey)
            {
                Trace("Steam.SetLobbyData", "key=" + key + "; value=" + value + "; success=" + success);
                return;
            }
            if (key == PublicPolicy.PublicationKey)
            {
                Trace("Steam.SetLobbyData", "key=" + key + "; validGeneration=" +
                    (Guid.TryParseExact(value, "N", out var generation) && generation != Guid.Empty) + "; success=" + success);
                return;
            }
            if (key != PublicPolicy.Marker && key != "version") return;
            string safe = key == PublicPolicy.Marker ? (value == "1" || value == "0" ? value : "invalid") : NumericVersion(value);
            Trace("Steam.SetLobbyData", "key=" + key + "; value=" + safe + "; success=" + success);
        }

        internal void RecordLeave(Lobby lobby)
        {
            if (IsDiagnosticLobby(lobby)) Trace("Steam.LeaveLobby", "native leave call completed");
        }

        internal void NativeCreationFailure(Exception? error)
        {
            if (error != null) Trace("native-OnLobbyCreated-failure", "exception=" + error.GetType().Name + "; native exception preserved");
        }

        private static string NumericVersion(string value) => int.TryParse(value, out int number) && number > 0
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : (value.Length == 0 ? "missing" : "invalid");

        private void ScheduleLobbySnapshot(string reason, float delay)
        {
            snapshotReason = reason;
            snapshotAt = Time.unscaledTime + delay;
        }

        private void PollDiagnostics()
        {
            try { PollDiagnosticsCore(); }
            catch (Exception error)
            {
                snapshotAt = -1;
                cleanupAt = -1;
                nextReadinessPoll = Time.unscaledTime + 2;
                Trace("diagnostics-failure", "exception=" + error.GetType().Name);
            }
        }

        private void PollDiagnosticsCore()
        {
            if (Time.unscaledTime >= nextReadinessPoll)
            {
                nextReadinessPoll = Time.unscaledTime + 0.5f;
                TryLiveBuild(out _, "poll");
            }
            if (snapshotAt >= 0 && Time.unscaledTime >= snapshotAt)
            {
                snapshotAt = -1;
                LogLobbySnapshot(snapshotReason);
                LogNativeControls();
            }
            if (cleanupAt >= 0 && Time.unscaledTime >= cleanupAt)
            {
                cleanupAt = -1;
                Trace("disconnect-settled", "sequence=" + cleanupSequence + "; currentLobby=" +
                    (SteamNetworkManager.instance != null && SteamNetworkManager.instance.CurrentLobby.HasValue) +
                    "; hostReference=" + (hostedLobby != null) + "; advertisementReference=" + (advertisement != null) +
                    "; publicResults=" + publicResults.Count + "; publicScopes=" + publicScopes.Count + "; joinPending=" + (pendingJoin != null) +
                    "; controlledConnection=" + (controlledConnectedId != 0) +
                    "; listening=" + (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) +
                    "; publicRequested=" + publicHosting.Value + "; queryBusy=" + publicQueries.IsBusy);
            }
        }

        private void LogLobbySnapshot(string reason)
        {
            if (!diagnosticLobby.HasValue) { Trace("snapshot/" + reason, "no tracked host lobby"); return; }
            try
            {
                var lobby = diagnosticLobby.Value;
                bool live = TryLiveBuild(out string build, "snapshot");
                string version = NumericVersion(lobby.GetData("version"));
                string marker = lobby.GetData(PublicPolicy.Marker);
                marker = marker == "0" || marker == "1" ? marker : marker.Length == 0 ? "missing" : "invalid";
                Trace("snapshot/" + reason, "owner=" + (SteamClient.IsValid && lobby.IsOwnedBy(SteamClient.SteamId)) +
                    "; lastAcceptedSteamType=" + acceptedType + "; lastAcceptedJoinable=" +
                    (acceptedJoinable.HasValue ? acceptedJoinable.Value.ToString() : "unobserved") +
                    "; marker=" + marker + "; state=" + lobby.GetData(PublicPolicy.StateKey) +
                    "; publicJoinable=" + lobby.GetData(PublicPolicy.JoinableKey) + "; phase=" + hostPhase +
                    "; lobbyLoadConfirmed=" + lobbyLoadConfirmed + "; version=" + version + "; liveBuild=" + (live ? build : "unavailable") +
                    "; versionMatches=" + (live && version == build) + "; publicationValid=" +
                    (Guid.TryParseExact(lobby.GetData(PublicPolicy.PublicationKey), "N", out var generation) && generation != Guid.Empty) + "; members=" + lobby.MemberCount +
                    "; memberLimit=" + lobby.MaxMembers + "; nativeSelection=" +
                    (GameManager.instance == null ? "unavailable" : NativeFriendsOnly ? "FriendsOnly" : "Private") +
                    "; publicRequested=" + publicHosting.Value + "; advertised=" + IsPublicHost);
            }
            catch (Exception error) { Trace("snapshot-failure", "exception=" + error.GetType().Name); }
        }

        private void RememberRetiredHost()
        {
            if (diagnosticLobby.HasValue) retiredHosts.Add(diagnosticLobby.Value.Id);
        }

        internal void AfterDisconnect()
        {
            Trace("disconnect-return", "currentLobby=" +
                (SteamNetworkManager.instance != null && SteamNetworkManager.instance.CurrentLobby.HasValue) +
                "; hostReference=" + (hostedLobby != null) + "; advertisementReference=" + (advertisement != null));
            diagnosticLobby = null;
            snapshotAt = -1;
            cleanupSequence = hostSequence;
            cleanupAt = Time.unscaledTime + 1;
        }

        private void LogDiscoveryProbe(List<Lobby> results)
        {
            bool current = diagnosticLobby.HasValue && results.Any(l => (ulong)l.Id == (ulong)diagnosticLobby.Value.Id);
            bool retired = results.Any(l => retiredHosts.Contains(l.Id));
            Trace("filtered-discovery", "resultCount=" + results.Count + "; containsCurrentHost=" + current +
                "; containsRetiredHost=" + retired + "; markerFilter=1; matchingLiveBuild=True; slotsAtLeast=1");
        }

        private LobbySettingsScreen? NativeSetup()
        {
            try { return GameCanvas.instance == null ? null : GameCanvas.instance.GetScreen<LobbySettingsScreen>(); }
            catch { return null; }
        }

        private void LogNativeControls()
        {
            var screen = NativeSetup();
            Trace("native-visibility-ui", "setupExists=" + (screen != null) + "; setupActive=" + (screen != null && screen.isActiveAndEnabled) +
                "; text=" + UiState(screen == null ? null : screen.LobbyVisibilityText) +
                "; left=" + UiState(screen == null ? null : screen.ChangeVisibilityButtonLeft) +
                "; right=" + UiState(screen == null ? null : screen.ChangeVisibilityButtonRight));
            var objects = UnityEngine.Object.FindObjectsByType<ChangeLobbyVisibilityInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Trace("native-visibility-world", "instances=" + objects.Length + "; active=" + objects.Count(o => o.isActiveAndEnabled));
        }

        private static string UiState(Component? element) => element == null ? "missing" :
            "self=" + element.gameObject.activeSelf + ",hierarchy=" + element.gameObject.activeInHierarchy;

        private Transform? VisibilityRow(LobbySettingsScreen screen)
        {
            if (screen.ChangeVisibilityButtonLeft == null || screen.ChangeVisibilityButtonRight == null || screen.LobbyVisibilityText == null) return null;
            for (var row = screen.ChangeVisibilityButtonLeft.transform.parent; row != null && row != screen.transform; row = row.parent)
                if (row.IsChildOf(screen.transform) && screen.ChangeVisibilityButtonRight.transform.IsChildOf(row) &&
                    screen.LobbyVisibilityText.transform.IsChildOf(row)) return row;
            return null;
        }

        private void RevealNativeRow()
        {
            var screen = NativeSetup();
            if (!HostReady || screen == null || !screen.isActiveAndEnabled) return;
            var row = VisibilityRow(screen);
            if (row == null) { SetStatus("A safe native visibility row was not identified; no UI objects were changed."); return; }
            foreach (var control in new Component[] { screen.LobbyVisibilityText, screen.ChangeVisibilityButtonLeft, screen.ChangeVisibilityButtonRight })
            {
                for (var node = control.transform; node != null && node != screen.transform; node = node.parent)
                {
                    if (!node.gameObject.activeSelf)
                    {
                        if (!revealedControls.ContainsKey(node.gameObject)) revealedControls.Add(node.gameObject, false);
                        node.gameObject.SetActive(true);
                    }
                    if (node == row) break;
                }
            }
            Trace("native-visibility-row-revealed", "in-memory UI only; original button handlers and enablement retained");
            LogNativeControls();
        }

        private void RestoreRevealedControls()
        {
            foreach (var entry in revealedControls) if (entry.Key != null) entry.Key.SetActive(entry.Value);
            revealedControls.Clear();
        }

        private void DrawDiagnosticControls()
        {
            bool before = GUI.enabled;
            if (GUILayout.Button("Log lobby diagnostics")) { LogLobbySnapshot("manual"); LogNativeControls(); }
            GUI.enabled = before && HostReady;
            if (GUILayout.Button("Open native lobby setup"))
            {
                try
                {
                    GameCanvas.instance.SetScreen<LobbySettingsScreen>();
                    ScheduleLobbySnapshot("native-setup-opened", 0.5f);
                }
                catch (Exception error) { SetStatus("Native setup could not open: " + error.GetType().Name); }
            }
            var screen = NativeSetup();
            GUI.enabled = before && HostReady && screen != null && screen.isActiveAndEnabled && VisibilityRow(screen) != null;
            if (GUILayout.Button("Reveal native visibility row (local test)")) RevealNativeRow();
            GUI.enabled = before;
        }
    }
}
