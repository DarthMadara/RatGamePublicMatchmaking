using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RatGamePublicMatchmaking
{
    internal static class LiveBuild
    {
        public static bool TryRead(bool clientReady, Func<int> read, out string build, out string reason)
        {
            build = "";
            reason = "client-not-ready";
            if (!clientReady) return false;
            try
            {
                int value = read();
                if (value <= 0) { reason = "build-not-positive"; return false; }
                build = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                reason = "ready";
                return true;
            }
            catch (Exception error) { reason = error.GetType().Name; return false; }
        }
    }

    internal static class PublicPolicy
    {
        public const string Marker = "ratgame.public";
        public const string PublicationKey = "ratgame.publication";
        public const string StateKey = "ratgame.state";
        public const string JoinableKey = "ratgame.joinable";

        public static bool IsCandidate(ulong id, string marker, string version, string build,
            int members, int capacity, bool alreadyJoined = false)
        {
            return id != 0 && marker == "1" && !string.IsNullOrEmpty(build) && version == build &&
                members >= 0 && capacity > 0 && (alreadyJoined ? members <= capacity : members < capacity);
        }
    }

    internal readonly struct PublicLobbySnapshot
    {
        public readonly ulong Id, Owner;
        public readonly string Marker, Version, Publication, State, Joinable;
        public readonly int Members, Capacity;
        public PublicLobbySnapshot(ulong id, ulong owner, string marker, string version, string publication, int members, int capacity,
            string state, string joinable)
        { Id = id; Owner = owner; Marker = marker; Version = version; Publication = publication; Members = members; Capacity = capacity;
            State = state; Joinable = joinable; }
    }

    internal enum JoinRejection { None, Cancelled, IdentityChanged, NotPublic, PublicationChanged, BuildMismatch, InvalidCapacity, Full, MetadataUnavailable, NotWaiting, NotJoinable }

    // A row authorizes one publication period, not a mutable SDK cache entry.
    // This is concurrency metadata, never an authentication credential.
    internal sealed class PublicJoinScope
    {
        private readonly ulong id;
        private readonly string publication, build;
        private PublicJoinScope(PublicLobbySnapshot value, string liveBuild)
        { id = value.Id; publication = value.Publication; build = liveBuild; }
        public static bool TryCapture(PublicLobbySnapshot value, string liveBuild, out PublicJoinScope? scope)
        {
            scope = null;
            if (value.State != "waiting" || value.Joinable != "1" || value.Id == 0 || value.Members <= 0 || !Guid.TryParseExact(value.Publication, "N", out var nonce) || nonce == Guid.Empty ||
                !int.TryParse(liveBuild, out int version) || version <= 0 ||
                !PublicPolicy.IsCandidate(value.Id, value.Marker, value.Version, liveBuild, value.Members, value.Capacity)) return false;
            scope = new PublicJoinScope(value, liveBuild);
            return true;
        }
        public JoinRejection Validate(PublicLobbySnapshot value, string liveBuild, bool alreadyJoined)
        {
            if (value.Id != id) return JoinRejection.IdentityChanged;
            if (value.Marker != "1") return JoinRejection.NotPublic;
            if (value.State != "waiting") return JoinRejection.NotWaiting;
            if (value.Joinable != "1") return JoinRejection.NotJoinable;
            if (value.Publication != publication) return JoinRejection.PublicationChanged;
            if (liveBuild != build || value.Version != build) return JoinRejection.BuildMismatch;
            if (value.Members <= 0 || value.Capacity <= 0) return JoinRejection.InvalidCapacity;
            if (alreadyJoined ? value.Members > value.Capacity : value.Members >= value.Capacity) return JoinRejection.Full;
            return JoinRejection.None;
        }
    }

    internal interface IHostLobby
    {
        bool IsOwner { get; }
        bool SetData(string key, string value);
        bool SetPublic();
        bool SetNative(bool friendsOnly);
        bool SetJoinable(bool value);
    }

    // The marker is the final publication step and the first withdrawal step.
    // A failed withdrawal closes admission and is retried; it never republishes.
    internal sealed class Advertisement
    {
        private readonly IHostLobby lobby;
        public bool IsPublic { get; private set; }
        public bool NeedsCleanup { get; private set; }
        public string Error { get; private set; } = "";

        public Advertisement(IHostLobby lobby) { this.lobby = lobby; }

        public bool Publish(string build, bool nativeFriendsOnly, HostPhase phase)
        {
            if (phase != HostPhase.Waiting || !lobby.IsOwner || !int.TryParse(build, out int buildId) || buildId <= 0) return false;
            if ((IsPublic || NeedsCleanup) && !Withdraw(nativeFriendsOnly)) return false;
            Error = "";
            if (lobby.SetData("version", build) &&
                lobby.SetData(PublicPolicy.PublicationKey, Guid.NewGuid().ToString("N")) &&
                lobby.SetData(PublicPolicy.StateKey, "waiting") && lobby.SetPublic())
            {
                IsPublic = true; // Actual Steam visibility, even if advertising subsequently fails.
                if (lobby.SetJoinable(true) && lobby.SetData(PublicPolicy.JoinableKey, "1") && lobby.SetData(PublicPolicy.Marker, "1"))
                {
                    NeedsCleanup = false;
                    return true;
                }
            }
            Withdraw(nativeFriendsOnly);
            Error = "Steam refused public visibility or metadata; publication was not completed.";
            return false;
        }

        public bool PrepareNativeChange()
        {
            if (!lobby.IsOwner) return false;
            bool removed = lobby.SetData(PublicPolicy.Marker, "0");
            lobby.SetData(PublicPolicy.JoinableKey, "0");
            bool closed = lobby.SetJoinable(false);
            NeedsCleanup = true;
            if (removed || closed) { IsPublic = false; return true; }
            Error = "Steam refused both withdrawal and admission closure; visibility change was stopped.";
            return false;
        }

        public bool Withdraw(bool nativeFriendsOnly, string state = "unknown")
        {
            if (!lobby.IsOwner) return false;
            NeedsCleanup = true;
            if (!lobby.SetData(PublicPolicy.Marker, "0"))
            {
                if (lobby.SetJoinable(false)) IsPublic = false;
                Error = "Public marker withdrawal failed; admission is being closed and cleanup will retry.";
                return false;
            }
            IsPublic = false;
            bool metadata = lobby.SetData(PublicPolicy.JoinableKey, "0") && lobby.SetData(PublicPolicy.StateKey, state);
            // Steam's global false blocks friends/invites too. Normal withdrawal restores
            // native visibility and leaves that path open; only failed cleanup closes it.
            if (!lobby.SetNative(nativeFriendsOnly) || !metadata || !lobby.SetJoinable(true))
            {
                lobby.SetJoinable(false);
                Error = "Native visibility restoration failed; cleanup will retry.";
                return false;
            }
            NeedsCleanup = false;
            Error = "";
            return true;
        }
    }

    internal static class Deadline
    {
        public static async Task<T> Wait<T>(Task<T> operation, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var delay = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var timer = Task.Delay(timeout, delay.Token);
                var winner = await Task.WhenAny(operation, timer);
                token.ThrowIfCancellationRequested();
                if (winner != operation) throw new TimeoutException("Steam request timed out.");
                delay.Cancel();
                return await operation;
            }
        }
    }

    // Timeout cancels the wait, not the Steam operation. Keep the slot until the
    // real operation settles so Steam's global query state cannot overlap.
    internal sealed class RequestLane<T>
    {
        private Task<T>? pending;
        public bool IsBusy => pending != null;

        public Task<T> Run(Func<Task<T>> start, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (pending != null) throw new InvalidOperationException("A previous Steam request is still pending.");
            var operation = start();
            pending = operation;
            _ = Observe(operation);
            return Deadline.Wait(operation, timeout, token);
        }

        private async Task Observe(Task<T> operation)
        {
            try { await operation; }
            catch { /* Caller observes failures; late faults must also be consumed. */ }
            finally { if (ReferenceEquals(pending, operation)) pending = null; }
        }
    }

    internal sealed class JoinLease
    {
        public readonly ulong Id;
        private readonly PublicJoinScope? scope;
        private ulong boundOwner;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public int Sequence { get; set; }
        public long ElapsedMilliseconds => clock.ElapsedMilliseconds;
        public bool Cancelled;
        public bool MetadataValidated { get; set; }
        public bool ConnectionConfirmed { get; set; }
        public bool MembershipAccepted { get; set; }
        public bool DeferredStarted { get; private set; }
        public JoinRejection Rejection { get; private set; }
        public bool AllowNativeEntry;
        public bool MembershipIssued;
        public bool EntryObserved;
        public bool NeedsEntryQuarantine => MembershipIssued && !EntryObserved;
        public JoinLease(ulong id, PublicJoinScope? scope = null) { Id = id; this.scope = scope; }
        public void Revoke(JoinRejection reason)
        {
            Cancelled = true;
            if (Rejection == JoinRejection.None) Rejection = reason;
        }
        public bool Revalidate(PublicLobbySnapshot value, string liveBuild, bool alreadyJoined, ulong localUser = 0)
        {
            if (Cancelled) return false;
            var reason = scope?.Validate(value, liveBuild, alreadyJoined) ?? JoinRejection.MetadataUnavailable;
            if (reason != JoinRejection.None) Revoke(reason);
            // GetLobbyOwner is only defined for actual lobby members. Pin it after
            // ordinary Steam entry, then require it unchanged through native start.
            if (!Cancelled && MembershipAccepted)
            {
                if (value.Owner == 0 || value.Owner == localUser || (boundOwner != 0 && boundOwner != value.Owner)) Revoke(JoinRejection.IdentityChanged);
                else boundOwner = value.Owner;
            }
            return !Cancelled;
        }
        public bool SuppressEntry(ulong id) => Id == id && (Cancelled || !AllowNativeEntry);
        public Action GuardDeferred(Action start, Func<bool> isCurrent, Func<bool> isAuthorized, Action rejected) => () =>
        {
            if (DeferredStarted) return;
            if (!Cancelled && isCurrent() && isAuthorized() && !Cancelled)
            { DeferredStarted = true; start(); }
            else { Revoke(JoinRejection.Cancelled); rejected(); }
        };
    }
}
