using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private JoinLease? pendingJoin;
        private CancellationTokenSource? joinCancellation;
        private ulong dispatchPublicId;
        private PublicJoinScope? dispatchPublicScope;
        private int joinSequence;
        private ulong controlledConnectedId;
        private readonly HashSet<ulong> entryQuarantine = new HashSet<ulong>();

        private void BeginPublicJoin(Lobby lobby, PublicJoinScope scope)
        {
            if (!CanJoin || pendingJoin != null || entryQuarantine.Contains(lobby.Id))
            { SetStatus("Disconnect first, or wait for the previous Steam entry callback before retrying this lobby."); return; }
            // Reuse the native entry point, but scope the bounded implementation to this public click.
            dispatchPublicId = lobby.Id;
            dispatchPublicScope = scope;
            try { SteamNetworkManager.instance.JoinLobby(lobby.Id); }
            finally { dispatchPublicId = 0; dispatchPublicScope = null; }
        }

        internal bool NativeJoinRequested(SteamNetworkManager manager, SteamId id)
        {
            if (dispatchPublicId != 0 && dispatchPublicId == (ulong)id)
            {
                if (pendingJoin == null && dispatchPublicScope != null) _ = JoinPublic(manager, new Lobby(id), dispatchPublicScope);
                return false;
            }
            // An uncancellable Steam join must settle before another membership request.
            if (pendingJoin != null) { SetStatus("A public join is still settling; try the native join again shortly."); return false; }
            if (entryQuarantine.Contains(id))
            { SetStatus("Still waiting for an old Steam entry callback for this lobby; retry later or restart the game."); return false; }
            return true;
        }

        internal bool AllowNativeInvite(Lobby lobby)
        {
            if (pendingJoin == null && !entryQuarantine.Contains(lobby.Id)) return true;
            SetStatus("A public join is still settling; accept the invite again after it finishes.");
            return false;
        }

        internal bool AllowLobbyEntry(Lobby lobby)
        {
            if (pendingJoin != null && (ulong)lobby.Id == pendingJoin.Id)
            {
                if (pendingJoin.AllowNativeEntry && !pendingJoin.Cancelled) return true;
                pendingJoin.EntryObserved = true;
                return false;
            }
            // Steam's event may arrive after its Task result. Keep an ID-specific
            // tombstone across disconnect until that automatic callback is consumed.
            if (entryQuarantine.Remove(lobby.Id)) return false;
            // Suppress a duplicate Steam callback after our explicit successful handoff.
            return controlledConnectedId == 0 || controlledConnectedId != (ulong)lobby.Id;
        }

        private bool schedulingPublicConnection;

        internal void GuardDeferredConnection(ref Action action)
        {
            var lease = pendingJoin;
            if (!schedulingPublicConnection || lease == null) return;
            action = lease.GuardDeferred(action,
                () => ReferenceEquals(pendingJoin, lease) && !shuttingDown,
                () => ValidatePublicAttempt(lease, "deferred-start"),
                () =>
                {
                    TraceJoin(lease, "deferred-start-suppressed");
                    AbortPublicAttempt(lease, JoinRejection.Cancelled);
                });
        }

        private void TraceJoin(JoinLease lease, string stage) => Logger.LogInfo(
            "[join#" + lease.Sequence + " +" + lease.ElapsedMilliseconds + "ms] " + stage + "; reason=" + lease.Rejection);

        private void AbortPublicAttempt(JoinLease lease, JoinRejection reason)
        {
            lease.Revoke(reason);
            if (!ReferenceEquals(pendingJoin, lease)) return;
            if (joinCancellation?.IsCancellationRequested == false)
            {
                TraceJoin(lease, "invalidated");
                joinCancellation.Cancel();
            }
        }

        // Use the latest locally received Steam member metadata; never block a native
        // transition callback on an async operation or call its native action twice.
        private bool ValidatePublicAttempt(JoinLease lease, string stage)
        {
            if (!ReferenceEquals(pendingJoin, lease) || shuttingDown || lease.Cancelled) return false;
            try
            {
                if (!TryLiveBuild(out string build, "public-join-" + stage))
                    lease.Revoke(JoinRejection.MetadataUnavailable);
                else
                {
                    var snapshot = ReadPublicSnapshot(new Lobby(lease.Id), lease.MembershipAccepted);
                    if (lease.Revalidate(snapshot, build, lease.MembershipIssued,
                        lease.MembershipAccepted ? (ulong)SteamClient.SteamId : 0))
                    { if (stage != "poll" && stage != "metadata-event") TraceJoin(lease, stage); return true; }
                }
            }
            catch { lease.Revoke(JoinRejection.MetadataUnavailable); }
            AbortPublicAttempt(lease, lease.Rejection);
            return false;
        }

        private void PollPublicJoin()
        {
            var lease = pendingJoin;
            if (lease != null && lease.MetadataValidated && !lease.ConnectionConfirmed && !lease.Cancelled)
                ValidatePublicAttempt(lease, "poll");
        }

        private void CancelPublicJoin()
        {
            if (pendingJoin != null) pendingJoin.Revoke(JoinRejection.Cancelled);
            joinCancellation?.Cancel();
        }

        private async Task JoinPublic(SteamNetworkManager manager, Lobby lobby, PublicJoinScope scope)
        {
            var lease = new JoinLease(lobby.Id, scope) { Sequence = ++joinSequence };
            pendingJoin = lease;
            joinCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = joinCancellation.Token;
            Task<Lobby?>? membership = null;
            bool handedOff = false;
            bool lateCleanup = false;
            Action<Lobby> onMetadataChanged = updated =>
            {
                if ((ulong)updated.Id == lease.Id && lease.MetadataValidated && !lease.ConnectionConfirmed)
                    ValidatePublicAttempt(lease, "metadata-event");
            };
            SteamMatchmaking.OnLobbyDataChanged += onMetadataChanged;
            try
            {
                TraceJoin(lease, "begin");
                if (!CanJoin) throw new InvalidOperationException("Leave the current session before joining.");
                SetStatus("Checking public lobby metadata...");
                await RefreshMetadata(lobby, token);
                token.ThrowIfCancellationRequested();
                lease.MetadataValidated = true;
                if (!ValidatePublicAttempt(lease, "before-membership"))
                    throw new OperationCanceledException("Public authorization changed before Steam entry.", token);
                if (!CanJoin) throw new InvalidOperationException("Another connection has started.");

                // Normal Steam membership request: access controls and authentication are unchanged.
                membership = SteamMatchmaking.JoinLobbyAsync(lobby.Id);
                lease.MembershipIssued = true;
                TraceJoin(lease, "membership-requested");
                var joined = await Deadline.Wait(membership, RequestTimeout, token);
                if (!joined.HasValue) throw new InvalidOperationException("Steam refused lobby entry (closed/full/unavailable).");
                lease.MembershipAccepted = true;
                token.ThrowIfCancellationRequested();
                // Membership data is supplied by normal Steam entry. Recheck the row's
                // immutable publication/owner scope before the original game handoff.
                if ((ulong)joined.Value.Id != lease.Id) throw new InvalidOperationException("Steam returned an unexpected lobby.");
                if (!ValidatePublicAttempt(lease, "after-membership"))
                    throw new OperationCanceledException("Public authorization changed during Steam entry.", token);
                if (!CanJoin) throw new InvalidOperationException("Another connection has started.");

                var network = NetworkManager.Singleton;
                var connected = new TaskCompletionSource<bool>();
                Action<ulong> onConnected = id =>
                {
                    if (id != network.LocalClientId || !network.IsConnectedClient) return;
                    if (!ValidatePublicAttempt(lease, "local-client-connected"))
                    { connected.TrySetException(new InvalidOperationException("Public authorization changed before connection completed.")); return; }
                    lease.ConnectionConfirmed = true;
                    connected.TrySetResult(true);
                };
                Action<ulong> onDisconnected = id =>
                {
                    if (id == network.LocalClientId) connected.TrySetException(new InvalidOperationException("Netcode disconnected during the join."));
                };
                network.OnClientConnectedCallback += onConnected;
                network.OnClientDisconnectCallback += onDisconnected;
                try
                {
                    // The automatic callback was gated. Reuse the game's existing owner-ID,
                    // transition, transport, scene-sync, and StartClient path exactly once.
                    lease.AllowNativeEntry = true;
                    controlledConnectedId = lobby.Id;
                    handedOff = true;
                    schedulingPublicConnection = true;
                    TraceJoin(lease, "native-handoff");
                    try { EnterMethod.Invoke(manager, new object[] { joined.Value }); }
                    finally { lease.AllowNativeEntry = false; schedulingPublicConnection = false; }
                    SetStatus("Steam lobby joined; waiting for Netcode...");
                    await Deadline.Wait(connected.Task, TimeSpan.FromSeconds(25), token);
                    TraceJoin(lease, "connection-confirmed");
                    SetStatus("Connected to public host. Multiplayer behavior has not been independently validated.");
                }
                finally
                {
                    network.OnClientConnectedCallback -= onConnected;
                    network.OnClientDisconnectCallback -= onDisconnected;
                    // Observe a fault even if cancellation/timeout won the race.
                    if (connected.Task.IsFaulted) _ = connected.Task.Exception;
                }
            }
            catch (Exception error)
            {
                lease.Revoke(JoinRejection.Cancelled);
                TraceJoin(lease, "failed-or-cancelled");
                if (!shuttingDown) SetStatus("Public join: " + (error is OperationCanceledException
                    ? "cancelled (" + lease.Rejection + ")." : error.Message));
                if (handedOff)
                {
                    if (!shuttingDown && GameManager.instance != null)
                        GameManager.instance.DisconnectAndRestart(false);
                    else
                        manager.Disconnect();
                }
                else if (membership != null)
                {
                    // Steam calls cannot be cancelled. Keep the guard/busy lease until the
                    // real response arrives, and leave any late successful membership.
                    lateCleanup = true;
                    _ = LeaveLateMembership(lease, membership);
                }
            }
            finally
            {
                SteamMatchmaking.OnLobbyDataChanged -= onMetadataChanged;
                if (!lateCleanup) ReleaseJoin(lease);
            }
        }

        private async Task RefreshMetadata(Lobby lobby, CancellationToken token)
        {
            var refreshed = new TaskCompletionSource<bool>();
            Action<Lobby> handler = updated =>
            {
                if ((ulong)updated.Id == (ulong)lobby.Id) refreshed.TrySetResult(true);
            };
            SteamMatchmaking.OnLobbyDataChanged += handler;
            try
            {
                if (!lobby.Refresh()) throw new InvalidOperationException("Steam rejected the metadata refresh.");
                await Deadline.Wait(refreshed.Task, RequestTimeout, token);
            }
            finally { SteamMatchmaking.OnLobbyDataChanged -= handler; }
        }

        private async Task LeaveLateMembership(JoinLease lease, Task<Lobby?> operation)
        {
            try
            {
                var result = await operation;
                if (result.HasValue) { result.Value.Leave(); TraceJoin(lease, "late-membership-left"); }
            }
            catch (Exception error) { Logger.LogWarning("Late public-join cleanup: " + error.Message); }
            finally { ReleaseJoin(lease); }
        }

        private void ReleaseJoin(JoinLease lease)
        {
            if (!ReferenceEquals(pendingJoin, lease)) return;
            TraceJoin(lease, "released");
            if (lease.NeedsEntryQuarantine) entryQuarantine.Add(lease.Id);
            pendingJoin = null;
            joinCancellation?.Dispose();
            joinCancellation = null;
            FinishShutdown();
        }
    }
}
