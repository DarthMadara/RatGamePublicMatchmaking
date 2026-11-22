using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private GameManager? lifecycleManager;
        private bool lobbyLoadConfirmed;
        private HostPhase hostPhase;
        private HostPhase appliedHostPhase = (HostPhase)(-1);

        private void TrackHost(GameManager manager)
        {
            if (lifecycleManager == manager) return;
            lifecycleManager = manager;
            lobbyLoadConfirmed = false;
            appliedHostPhase = (HostPhase)(-1);
        }

        internal void BeforeMatchStart(GameManager manager)
        {
            if (!HostReady || !manager.IsServer || System.Convert.ToInt32(RpcStage.GetValue(manager)) != 1) return;
            TrackHost(manager);
            lobbyLoadConfirmed = false;
            ApplyHostPhase(HostPhase.Starting); // Before native state write / loading-screen RPC.
        }

        internal void BeforeLobbyReturn(GameManager manager)
        {
            if (!HostReady || !manager.IsServer) return;
            TrackHost(manager);
            lobbyLoadConfirmed = false;
            ApplyHostPhase(HostPhase.Unknown); // InLobby is written BEFORE NGO starts loading.
        }

        internal void HostSceneCompleted(GameManager manager, string scene, List<ulong> timedOut)
        {
            if (!HostReady || !manager.IsServer) return;
            TrackHost(manager);
            lobbyLoadConfirmed = timedOut != null && timedOut.Count == 0 && scene == manager.LobbyScene &&
                manager.CurrentGameState.Value == GameState.InLobby && SceneManager.GetActiveScene().name == scene;
            PollHostLifecycle();
        }

        private void PollHostLifecycle()
        {
            var manager = GameManager.instance;
            if (manager != null && HostReady) TrackHost(manager);
            bool ready = manager != null && HostReady;
            var state = ready ? manager!.CurrentGameState.Value : GameState.MainMenu;
            var phase = HostPhasePolicy.Resolve(ready, hostedLobby?.IsOwner == true, ready && manager!.IsSpawned,
                state == GameState.InLobby, state == GameState.InitializingGame,
                state == GameState.InGame || state == GameState.GameEnd || state == GameState.ScoreScreen,
                lobbyLoadConfirmed, ready && SceneManager.GetActiveScene().name == manager!.LobbyScene,
                TransitionScreen.instance == null || TransitionScreen.instance.InTransition ||
                GameCanvas.instance == null || GameCanvas.instance.ShowLoadingScreen);
            if (!ready) { lifecycleManager = null; lobbyLoadConfirmed = false; }
            ApplyHostPhase(phase);
        }

        private void ApplyHostPhase(HostPhase phase)
        {
            hostPhase = phase;
            if (advertisement == null || hostedLobby == null || !hostedLobby.IsOwner || phase == appliedHostPhase) return;
            appliedHostPhase = phase;
            Logger.LogInfo("Host lifecycle=" + HostPhasePolicy.Metadata(phase) + "; Public Hosting requested=" + publicHosting.Value);
            if (phase == HostPhase.Waiting && publicHosting.Value && !advertisement.NeedsCleanup) Publish();
            else Withdraw();
        }
    }
}
