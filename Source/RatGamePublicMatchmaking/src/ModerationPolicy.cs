using System;

namespace RatGamePublicMatchmaking
{
    internal static class SteamAccount
    {
        // Public universe, individual desktop account, nonzero account number.
        internal static bool Valid(ulong id) => (id >> 56) == 1 && ((id >> 52) & 15) == 1 &&
            ((id >> 32) & 0xfffff) == 1 && (uint)id != 0;
    }

    internal readonly struct ModerationPeer : IEquatable<ModerationPeer>
    {
        internal readonly ulong ClientId, TransportId, SteamId;
        internal readonly long Generation;
        internal readonly string Name;
        internal ModerationPeer(ulong client, ulong transport, ulong steam, long generation, string name)
        { ClientId = client; TransportId = transport; SteamId = steam; Generation = generation; Name = name; }
        public bool Equals(ModerationPeer other) => ClientId == other.ClientId && TransportId == other.TransportId &&
            SteamId == other.SteamId && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is ModerationPeer peer && Equals(peer);
        public override int GetHashCode() => ClientId.GetHashCode() ^ TransportId.GetHashCode() ^ SteamId.GetHashCode() ^ Generation.GetHashCode();
    }

    internal interface IModerationSession
    {
        bool IsHost { get; }
        ulong HostClientId { get; }
        ulong HostSteamId { get; }
        bool Resolve(ulong client, out ModerationPeer peer);
        bool Disconnect(ModerationPeer peer);
    }

    internal enum ModerationResult { Requested, BannedDisconnectFailed, NotHost, Self, StalePlayer, InvalidIdentity, ConfirmationRequired, StorageFailed, DisconnectFailed }

    // Local UI calls only. No RPC, custom message, payload, console or remote-request handler.
    internal sealed class HostModerator
    {
        private readonly IModerationSession session;
        private readonly BanStore bans;
        internal HostModerator(IModerationSession session, BanStore bans) { this.session = session; this.bans = bans; }
        private ModerationResult? Validate(ModerationPeer selected)
        {
            if (!session.IsHost) return ModerationResult.NotHost;
            if (selected.ClientId == session.HostClientId || selected.SteamId == session.HostSteamId) return ModerationResult.Self;
            if (!SteamAccount.Valid(selected.SteamId)) return ModerationResult.InvalidIdentity;
            if (!session.Resolve(selected.ClientId, out var live) || !live.Equals(selected)) return ModerationResult.StalePlayer;
            return null;
        }
        internal ModerationResult Kick(ModerationPeer selected)
        {
            var denied = Validate(selected);
            if (denied.HasValue) return denied.Value;
            try { return session.Disconnect(selected) ? ModerationResult.Requested : ModerationResult.DisconnectFailed; }
            catch { return ModerationResult.DisconnectFailed; }
        }
        internal ModerationResult Ban(ModerationPeer selected, bool confirmed)
        {
            var denied = Validate(selected);
            if (denied.HasValue) return denied.Value;
            if (!confirmed) return ModerationResult.ConfirmationRequired;
            // Commit first: admission sees the durable ban before any disconnect can race a rejoin.
            if (!bans.Add(selected.SteamId, selected.Name)) return ModerationResult.StorageFailed;
            // Identity/session may change while the file is committed; never act on a recycled client.
            denied = Validate(selected);
            if (denied.HasValue) return ModerationResult.BannedDisconnectFailed;
            try { return session.Disconnect(selected) ? ModerationResult.Requested : ModerationResult.BannedDisconnectFailed; }
            catch { return ModerationResult.BannedDisconnectFailed; }
        }
        internal bool Unban(ulong steam) => session.IsHost && SteamAccount.Valid(steam) && bans.Remove(steam);
    }
}
