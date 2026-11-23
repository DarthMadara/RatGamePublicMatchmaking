using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private BanStore bans = null!;
        private HostModerator moderator = null!;
        private SteamModerationSession moderationSession = null!;
        private readonly SteamConnections steamConnections = new SteamConnections();
        private FacepunchTransport? moderationTransport;
        private NetworkManager? moderationNetwork;
        private static readonly FieldInfo RemoteIdentityField = AccessTools.Field(typeof(ConnectionInfo), "identity");
        private static readonly FieldInfo SocketManagerField = AccessTools.Field(typeof(FacepunchTransport), "socketManager");
        internal static readonly Type ConnectionManagerType = typeof(NetworkManager).Assembly.GetType("Unity.Netcode.NetworkConnectionManager", true);
        private static readonly FieldInfo ApprovalNetworkField = AccessTools.Field(ConnectionManagerType, "NetworkManager");
        private string moderationStatus = "Select a player to manage.";
        private float nextBanCleanup;

        private void InitializeModeration()
        {
            if (RemoteIdentityField == null || RemoteIdentityField.FieldType != typeof(NetIdentity) ||
                SocketManagerField == null || SocketManagerField.FieldType != typeof(SocketManager) ||
                ApprovalNetworkField == null || ApprovalNetworkField.FieldType != typeof(NetworkManager))
                throw new MissingFieldException("Steam connection identity layout changed; moderation cannot authenticate players.");
            bans = new BanStore(new AtomicBanFile(Path.Combine(Paths.ConfigPath, Id, "bans.v1.xml")));
            moderationSession = new SteamModerationSession(this);
            moderator = new HostModerator(moderationSession, bans);
            if (!bans.Ready) { moderationStatus = bans.Error; Logger.LogError(bans.Error); }
        }

        private bool ModerationAuthority => !shuttingDown && HostReady && !NetworkManager.Singleton.ShutdownInProgress &&
            NetworkManager.Singleton.NetworkConfig.NetworkTransport is FacepunchTransport &&
            SteamNetworkManager.instance != null && SteamNetworkManager.instance.CurrentLobby.HasValue &&
            SteamNetworkManager.instance.CurrentLobby.Value.IsOwnedBy(SteamClient.SteamId);

        private bool IsModeratedTransport(FacepunchTransport transport) => !shuttingDown && NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsHost && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.ShutdownInProgress &&
            ReferenceEquals(NetworkManager.Singleton.NetworkConfig.NetworkTransport, transport);

        private void TrackModerationTransport(FacepunchTransport transport)
        {
            var network = NetworkManager.Singleton;
            if (ReferenceEquals(moderationTransport, transport) && ReferenceEquals(moderationNetwork, network)) return;
            ResetModerationConnections();
            moderationTransport = transport;
            moderationNetwork = network;
        }

        private static bool ReadRemoteSteam(ConnectionInfo info, out ulong steam)
        {
            steam = 0;
            // The installed wrapper's public Identity getter returns address, not identity.
            if (!(RemoteIdentityField.GetValue(info) is NetIdentity identity) || !identity.IsSteamId) return false;
            steam = identity.SteamId;
            return SteamAccount.Valid(steam);
        }

        internal bool AllowSteamConnecting(FacepunchTransport transport, Connection connection, ConnectionInfo info)
        {
            if (!IsModeratedTransport(transport)) return true;
            try
            {
                TrackModerationTransport(transport);
                if (!bans.Ready) return CloseRejected(transport, connection);
                // SDK may not know the peer yet. Require its identity at Connected instead.
                if (ReadRemoteSteam(info, out ulong steam) && (!bans.Allows(steam) || steam == (ulong)SteamClient.SteamId))
                    return CloseRejected(transport, connection);
                return true;
            }
            catch { return CloseRejected(transport, connection); }
        }

        internal bool AllowSteamConnected(FacepunchTransport transport, Connection connection, ConnectionInfo info)
        {
            if (!IsModeratedTransport(transport)) return true;
            try
            {
                TrackModerationTransport(transport);
                if (info.State != ConnectionState.Connected || !ReadRemoteSteam(info, out ulong steam) ||
                    steam == (ulong)SteamClient.SteamId || !bans.Allows(steam) || !steamConnections.Bind(connection.Id, steam))
                    return CloseRejected(transport, connection);
                // Captured BEFORE native InvokeOnTransportEvent creates the NGO pending client.
                return true;
            }
            catch { return CloseRejected(transport, connection); }
        }

        internal bool AllowSteamMessage(FacepunchTransport transport, Connection connection, NetIdentity identity)
        {
            if (!IsModeratedTransport(transport)) return true;
            try
            {
                if (!ReferenceEquals(moderationTransport, transport) || !ReferenceEquals(moderationNetwork, NetworkManager.Singleton) ||
                    !identity.IsSteamId || !steamConnections.AllowsMessage(connection.Id, identity.SteamId, bans)) return CloseRejected(transport, connection);
                return true;
            }
            catch { return CloseRejected(transport, connection); }
        }

        internal void GuardNgoApproval(object connectionManager, ulong client, NetworkManager.ConnectionApprovalResponse response)
        {
            var network = ApprovalNetworkField.GetValue(connectionManager) as NetworkManager;
            if (network == null || !network.IsHost || !network.IsListening || client == network.LocalClientId ||
                !(network.NetworkConfig.NetworkTransport is FacepunchTransport transport) || !IsModeratedTransport(transport)) return;
            bool allowed = false;
            try
            {
                ulong handle = network.GetTransportIdFromClientId(client);
                allowed = ReferenceEquals(network, moderationNetwork) && ReferenceEquals(transport, moderationTransport) &&
                    network.GetClientIdFromTransportId(handle) == client && steamConnections.AllowsAdmission(handle, bans);
            }
            catch { /* Missing/stale authenticated mapping denies remote approval. */ }
            if (allowed) return; // Never turn another approval handler's rejection into acceptance.
            response.Approved = false;
            response.Reason = "Host admission denied";
        }

        private bool CloseRejected(FacepunchTransport transport, Connection connection)
        {
            bool closed = false;
            try { closed = connection.Close(false, 1000, "Host admission denied"); }
            catch { moderationStatus = "Connection close failed; admission remains blocked."; }
            if (!closed) moderationStatus = "Connection close failed; admission remains blocked.";
            // A local Steam Close need not emit a local disconnect callback. Use NGO's
            // exact reverse mapping to clean any pending/connected client immediately.
            try
            {
                var network = NetworkManager.Singleton;
                ulong client = network.GetClientIdFromTransportId(connection.Id);
                if (client != network.LocalClientId && network.GetTransportIdFromClientId(client) == connection.Id)
                    network.DisconnectClient(client);
            }
            catch { /* An early rejected socket has no NGO mapping yet. */ }
            if (closed) RemoveClosedSocket(transport, connection);
            return false; // Never forward rejected Connect or Data even if Close fails.
        }

        private static void RemoveClosedSocket(FacepunchTransport transport, Connection connection)
        {
            // Mirror the wrapper's managed list cleanup; no synthetic SDK callback.
            try
            {
                if (!(SocketManagerField.GetValue(transport) is SocketManager socket)) return;
                socket.Connecting.Remove(connection);
                socket.Connected.Remove(connection);
            }
            catch { /* Cleanup failure must not interrupt native NGO disconnect processing. */ }
        }

        internal void SteamDisconnected(FacepunchTransport transport, Connection connection)
        {
            if (ReferenceEquals(transport, moderationTransport)) steamConnections.Forget(connection.Id);
        }

        internal void SteamRemoteClosed(FacepunchTransport transport, ulong handle)
        {
            if (!ReferenceEquals(transport, moderationTransport)) return;
            steamConnections.Forget(handle);
            if (handle > 0 && handle <= uint.MaxValue) RemoveClosedSocket(transport, (Connection)(uint)handle);
        }

        internal void SteamTransportStopped(FacepunchTransport transport)
        {
            if (ReferenceEquals(transport, moderationTransport)) ResetModerationConnections();
        }

        private void ResetModerationConnections()
        {
            steamConnections.Clear();
            moderationNetwork = null;
            moderationTransport = null;
            selectedPlayer = null;
            confirmBan = null;
        }

        private void PollModeration()
        {
            try
            {
                if (!HostReady || !ReferenceEquals(moderationNetwork, NetworkManager.Singleton))
                    ResetModerationConnections();
                if (ModerationAuthority && UnityEngine.Time.unscaledTime >= nextBanCleanup)
                {
                    nextBanCleanup = UnityEngine.Time.unscaledTime + 0.5f;
                    foreach (ulong client in NetworkManager.Singleton.ConnectedClientsIds.ToArray())
                        if (moderationSession.Resolve(client, out var peer) && !bans.Allows(peer.SteamId))
                        {
                            try { moderationSession.Disconnect(peer); }
                            catch { moderationStatus = "Ban remains active; disconnect cleanup will retry."; }
                        }
                }
                PollModerationUi();
            }
            catch (Exception error)
            {
                moderationStatus = "Player management unavailable (" + error.GetType().Name + ").";
                CloseModerationUi();
            }
        }

        private static string SteamDisplayName(ulong steam)
        {
            try { return BanStore.DisplayName(new Friend((SteamId)steam).Name); }
            catch { return "Steam account"; }
        }

        private sealed class SteamModerationSession : IModerationSession
        {
            private readonly Plugin plugin;
            internal SteamModerationSession(Plugin plugin) { this.plugin = plugin; }
            public bool IsHost => plugin.ModerationAuthority;
            public ulong HostClientId => NetworkManager.Singleton == null ? 0 : NetworkManager.Singleton.LocalClientId;
            public ulong HostSteamId => SteamClient.IsValid ? (ulong)SteamClient.SteamId : 0;
            public bool Resolve(ulong client, out ModerationPeer peer)
            {
                peer = default;
                var network = NetworkManager.Singleton;
                if (!IsHost || client == HostClientId || !ReferenceEquals(plugin.moderationNetwork, network) ||
                    !ReferenceEquals(plugin.moderationTransport, network.NetworkConfig.NetworkTransport) ||
                    !network.ConnectedClients.ContainsKey(client)) return false;
                ulong handle = network.GetTransportIdFromClientId(client);
                if (handle == 0 || network.GetClientIdFromTransportId(handle) != client ||
                    !plugin.steamConnections.TryGet(handle, out var binding)) return false;
                peer = new ModerationPeer(client, handle, binding.SteamId, binding.Generation, SteamDisplayName(binding.SteamId));
                return true;
            }
            public bool Disconnect(ModerationPeer peer)
            {
                if (!Resolve(peer.ClientId, out var current) || !peer.Equals(current) || peer.SteamId == HostSteamId) return false;
                NetworkManager.Singleton.DisconnectClient(peer.ClientId);
                return !NetworkManager.Singleton.ConnectedClients.ContainsKey(peer.ClientId);
            }
        }
    }

    [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnConnecting")]
    internal static class ModerationConnectingPatch
    {
        private static bool Prefix(FacepunchTransport __instance, Connection connection, ConnectionInfo info) =>
            Plugin.Instance?.AllowSteamConnecting(__instance, connection, info) ?? true;
    }
    [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnConnected")]
    internal static class ModerationConnectedPatch
    {
        private static bool Prefix(FacepunchTransport __instance, Connection connection, ConnectionInfo info) =>
            Plugin.Instance?.AllowSteamConnected(__instance, connection, info) ?? true;
    }
    [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnDisconnected")]
    internal static class ModerationDisconnectedPatch
    {
        private static void Prefix(FacepunchTransport __instance, Connection connection) => Plugin.Instance?.SteamDisconnected(__instance, connection);
    }
    [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnMessage")]
    internal static class ModerationMessagePatch
    {
        private static bool Prefix(FacepunchTransport __instance, Connection connection, NetIdentity identity) =>
            Plugin.Instance?.AllowSteamMessage(__instance, connection, identity) ?? true;
    }
    [HarmonyPatch(typeof(FacepunchTransport), nameof(FacepunchTransport.DisconnectRemoteClient))]
    internal static class ModerationRemoteClosedPatch
    {
        private static void Postfix(FacepunchTransport __instance, ulong clientId) => Plugin.Instance?.SteamRemoteClosed(__instance, clientId);
    }
    [HarmonyPatch(typeof(FacepunchTransport), nameof(FacepunchTransport.Shutdown))]
    internal static class ModerationTransportStoppedPatch
    {
        private static void Prefix(FacepunchTransport __instance) => Plugin.Instance?.SteamTransportStopped(__instance);
    }
    [HarmonyPatch]
    internal static class ModerationApprovalPatch
    {
        // NGO's connection manager is internal; resolve the inspected exact signature.
        private static MethodBase TargetMethod() => AccessTools.Method(Plugin.ConnectionManagerType, "HandleConnectionApproval",
            new[] { typeof(ulong), typeof(NetworkManager.ConnectionApprovalResponse) }) ??
            throw new MissingMethodException("NGO final approval contract changed.");
        private static void Prefix(object __instance, ulong __0, NetworkManager.ConnectionApprovalResponse __1) =>
            Plugin.Instance?.GuardNgoApproval(__instance, __0, __1);
    }
}
