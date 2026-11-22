using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RatGamePublicMatchmaking
{
    [BepInPlugin(Id, Name, "0.1.0")]
    [BepInProcess("Rat Game.exe")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.ratgame.publicmatchmaking";
        public const string Name = "Rat Game Public Matchmaking";
        internal static Plugin? Instance;
        private ConfigEntry<bool> publicHosting = null!;
        private Harmony harmony = null!;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Advertisement? advertisement;
        private SteamHostLobby? hostedLobby;
        private bool showPanel;
        private bool shuttingDown;
        private int settingsChanged;
        private float retryAt;
        private string status = "Public hosting is OFF. Use the game's normal Host button.";
        private Vector2 scroll;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        internal static readonly MethodInfo RenderMethod = AccessTools.Method(typeof(FindLobbyScreen),
            "CreateLobbyElements", new[] { typeof(Transform), typeof(List<Lobby>) });
        private static readonly MethodInfo EnterMethod = AccessTools.Method(typeof(SteamNetworkManager),
            "OnLobbyEntered", new[] { typeof(Lobby) });
        internal static readonly FieldInfo RpcStage = AccessTools.Field(typeof(NetworkBehaviour), "__rpc_exec_stage");

        private void Awake()
        {
            Instance = this;
            publicHosting = Config.Bind("Hosting", "PublicHosting", false,
                "Explicitly publish your own hosted lobby. OFF preserves the game's native modes. " +
                "Native visibility changes switch this OFF. F8 shows optional diagnostics.");
            nativeUiTools = Config.Bind("Diagnostics", "NativeUiTools", false,
                "Local development hierarchy capture and command files. Leave OFF for normal play.");
            publicHosting.SettingChanged += SettingChanged;
            if (publicHosting.Value) status = "Public Hosting is armed for your next hosted lobby.";
            try
            {
                if (RenderMethod == null || EnterMethod == null || RpcStage == null)
                    throw new MissingMemberException("The installed game's networking/UI contract changed.");
                harmony = new Harmony(Id);
                harmony.PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo(Name + " 0.1.0 (waiting-browser.1) loaded. Public Hosting=" + publicHosting.Value);
                PollDiagnostics();
            }
            catch (Exception error)
            {
                harmony?.UnpatchSelf();
                Instance = null;
                enabled = false;
                Logger.LogError(error);
            }
        }

        private void SettingChanged(object sender, EventArgs args) => Interlocked.Exchange(ref settingsChanged, 1);

        private static bool NativeFriendsOnly => GameManager.instance != null &&
            GameManager.instance.LobbyVisibilityVar.Value == LobbyVisibility.FriendsOnly;

        private static bool HostReady => SteamClient.IsValid && NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsHost && NetworkManager.Singleton.IsListening;

        private static bool CanJoin => SteamClient.IsValid && NetworkManager.Singleton != null &&
            !NetworkManager.Singleton.IsListening && SteamNetworkManager.instance != null &&
            !SteamNetworkManager.instance.CurrentLobby.HasValue;

        private void Update()
        {
            PollHostLifecycle();
            InspectNativeUi();
            PollNativeUi();
            PollDiagnostics();
            PollPublicJoin();
            if (Keyboard.current?.f8Key.wasPressedThisFrame == true)
            {
                showPanel = !showPanel;
                Logger.LogInfo("Prototype panel " + (showPanel ? "shown" : "hidden") + " via F8.");
            }
            if (Interlocked.Exchange(ref settingsChanged, 0) != 0 && advertisement != null && hostedLobby != null)
            {
                if (publicHosting.Value && HostReady && hostedLobby.IsOwner)
                    Publish();
                else if (!publicHosting.Value && (advertisement.IsPublic || advertisement.NeedsCleanup))
                    Withdraw();
            }
            if (advertisement != null && hostedLobby != null && advertisement.NeedsCleanup &&
                hostedLobby.IsOwner && Time.unscaledTime >= retryAt)
            {
                retryAt = Time.unscaledTime + 2;
                Withdraw();
                if (!advertisement.NeedsCleanup && publicHosting.Value && hostPhase == HostPhase.Waiting) Publish();
            }
        }

        internal void LobbyCreated(Result result, Lobby lobby)
        {
            if (result != Result.OK || !HostReady || !lobby.IsOwnedBy(SteamClient.SteamId)) return;
            hostedLobby = new SteamHostLobby(lobby, message => Logger.LogWarning(message));
            advertisement = new Advertisement(hostedLobby);
            Logger.LogInfo("Host lobby created. Native visibility selection=" +
                (NativeFriendsOnly ? "FriendsOnly" : "Private") + "; Public Hosting=" + publicHosting.Value +
                "; public marker='" + lobby.GetData(PublicPolicy.Marker) + "'.");
            appliedHostPhase = (HostPhase)(-1);
            PollHostLifecycle();
            ScheduleLobbySnapshot("created", 1.5f);
        }

        private void Publish()
        {
            if (advertisement == null || hostedLobby == null || !HostReady || !hostedLobby.IsOwner || advertisement.IsPublic) return;
            if (hostPhase != HostPhase.Waiting)
            {
                SetStatus("Public Hosting is armed; advertising resumes only in the waiting lobby.");
                return;
            }
            if (!TryLiveBuild(out string build, "publish"))
            {
                publicHosting.Value = false;
                SetStatus("Steam build ID is not ready; public hosting was not enabled. Retry after Steam initializes.");
                return;
            }
            bool success = advertisement.Publish(build, NativeFriendsOnly, hostPhase);
            if (!success) publicHosting.Value = false;
            SetStatus(success ? "Public hosting is ON; this lobby is advertised." : advertisement.Error);
            ScheduleLobbySnapshot("publish-result", 1.5f);
        }

        private void Withdraw()
        {
            if (advertisement == null) return;
            bool success = advertisement.Withdraw(NativeFriendsOnly, HostPhasePolicy.Metadata(hostPhase));
            SetStatus(success ? (publicHosting.Value ? "Public listing paused until the waiting lobby; friends/invites retain native behavior." :
                "Public hosting is OFF; native visibility restored.") : advertisement.Error);
            ScheduleLobbySnapshot("withdraw-result", 0.5f);
        }

        internal bool BeforeNativeVisibility()
        {
            Trace("native-visibility-before", "publicRequested=" + publicHosting.Value + "; advertised=" + IsPublicHost);
            publicHosting.Value = false;
            if (advertisement == null || (!advertisement.IsPublic && !advertisement.NeedsCleanup)) return true;
            bool safe = advertisement.PrepareNativeChange();
            if (!safe) SetStatus(advertisement.Error);
            return safe;
        }

        internal void AfterNativeVisibility()
        {
            if (advertisement != null && advertisement.NeedsCleanup) Withdraw();
            Logger.LogInfo("Native visibility selection=" + (NativeFriendsOnly ? "FriendsOnly" : "Private") +
                "; Public Hosting=" + publicHosting.Value + "; public advertisement active=" + IsPublicHost + ".");
            ScheduleLobbySnapshot("native-visibility-after", 1);
        }

        internal void Disconnecting()
        {
            LogLobbySnapshot("disconnect-before");
            RememberRetiredHost();
            RestoreRevealedControls();
            CancelPublicJoin();
            hostPhase = HostPhase.Unknown;
            if (advertisement != null && (advertisement.IsPublic || advertisement.NeedsCleanup)) Withdraw();
            advertisement = null;
            hostedLobby = null;
            lifecycleManager = null;
            lobbyLoadConfirmed = false;
            hostPhase = HostPhase.Unknown;
            appliedHostPhase = (HostPhase)(-1);
            controlledConnectedId = 0;
            publicResults.Clear();
            publicScopes.Clear();
            browserEpoch++;
            RenderAllPublicRows();
        }

        private void SetStatus(string message)
        {
            status = message;
            Logger.LogInfo(message);
        }

        private void OnGUI()
        {
            if (!showPanel || shuttingDown) return;
            float width = Math.Min(400, Screen.width - 20);
            GUILayout.BeginArea(new Rect(Math.Max(10, Screen.width - width - 10), 15, width,
                Math.Min(Screen.height - 30, 680)), GUI.skin.box);
            GUILayout.Label(Name + " — diagnostics (F8)");
            bool wasEnabled = GUI.enabled;
            GUI.enabled = SteamClient.IsValid && (HostReady || CanJoin) && pendingJoin == null;
            bool selected = GUILayout.Toggle(publicHosting.Value, "Public Hosting (opt-in)");
            if (selected != publicHosting.Value) publicHosting.Value = selected;
            GUI.enabled = wasEnabled;
            GUILayout.Label(advertisement != null && advertisement.IsPublic
                ? "Current host: PUBLIC" : "Use the game's native visibility controls for private/friends.");
            GUILayout.Label(status);
            if (pendingJoin != null && !pendingJoin.ConnectionConfirmed && GUILayout.Button("Cancel public join")) CancelPublicJoin();
            DrawDiagnosticControls();
            GUI.enabled = SteamClient.IsValid && !publicQueries.IsBusy && !shuttingDown;
            if (GUILayout.Button("Refresh public lobbies")) _ = RefreshPublic(null, browserEpoch);
            GUI.enabled = wasEnabled;
            // Fallback remains available if the shipped public container is absent/hidden.
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(130));
            foreach (var lobby in SteamClient.IsValid ? publicResults.ToArray() : Array.Empty<Lobby>())
            {
                if (!TryLiveBuild(out string displayBuild, "browser-display") || !Candidate(lobby, displayBuild) ||
                    !publicScopes.TryGetValue(lobby.Id, out var scope)) continue;
                GUI.enabled = CanJoin && pendingJoin == null;
                string label = lobby.GetData("name");
                if (label.Length > 80) label = label.Substring(0, 80);
                if (GUILayout.Button(label + "  " + lobby.MemberCount + "/" + lobby.MaxMembers))
                    BeginPublicJoin(lobby, scope);
            }
            GUI.enabled = wasEnabled;
            GUILayout.EndScrollView();
            GUILayout.Label("Only explicitly advertised lobbies with your Steam build are listed.");
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            shuttingDown = true;
            publicHosting.SettingChanged -= SettingChanged;
            Disconnecting();
            lifetime.Cancel();
            RestoreNativeUi();
            FinishShutdown();
        }

        private void FinishShutdown()
        {
            // Retain the entry guard until an uncancellable Steam join settles.
            if (!shuttingDown || pendingJoin != null) return;
            harmony?.UnpatchSelf();
            Instance = null;
            lifetime.Dispose();
        }

        private sealed class SteamHostLobby : IHostLobby
        {
            private Lobby lobby;
            private readonly Action<string> warn;
            public SteamHostLobby(Lobby lobby, Action<string> warn) { this.lobby = lobby; this.warn = warn; }
            public bool IsOwner
            {
                get
                {
                    try { return SteamClient.IsValid && lobby.IsOwnedBy(SteamClient.SteamId); }
                    catch { return false; }
                }
            }
            private bool Try(Func<bool> operation)
            {
                try { return IsOwner && operation(); }
                catch (Exception error) { warn("Steam lobby operation failed: " + error.Message); return false; }
            }
            public bool SetData(string key, string value) => Try(() => lobby.SetData(key, value));
            public bool SetPublic() => Try(() => lobby.SetPublic());
            public bool SetNative(bool friendsOnly) => Try(() => friendsOnly ? lobby.SetFriendsOnly() : lobby.SetInvisible());
            public bool SetJoinable(bool value) => Try(() => lobby.SetJoinable(value));
        }
    }
}
