namespace RatGamePublicMatchmaking
{
    internal enum HostPhase { Unknown, Waiting, Starting, InGame }

    internal static class HostPhasePolicy
    {
        internal static HostPhase Resolve(bool ready, bool owner, bool spawned, bool inLobby, bool initializing,
            bool playingOrEnded, bool lobbyLoadConfirmed, bool lobbySceneActive, bool transitioning)
        {
            if (!ready || !owner || !spawned) return HostPhase.Unknown;
            if (initializing) return HostPhase.Starting;
            if (playingOrEnded) return HostPhase.InGame;
            return inLobby && lobbyLoadConfirmed && lobbySceneActive && !transitioning ? HostPhase.Waiting : HostPhase.Unknown;
        }

        internal static string Metadata(HostPhase phase) => phase == HostPhase.Waiting ? "waiting" :
            phase == HostPhase.Starting ? "starting" : phase == HostPhase.InGame ? "ingame" : "unknown";
    }
}
