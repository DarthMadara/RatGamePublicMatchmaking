using RatGamePublicMatchmaking;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

try
{
var native = new FakeLobby();
int buildReads = 0;
Check(!LiveBuild.TryRead(false, () => { buildReads++; return 123; }, out string liveBuild, out _) &&
    liveBuild == "" && buildReads == 0, "Steam build is never read before client readiness");
Check(!LiveBuild.TryRead(true, () => throw new NullReferenceException(), out liveBuild, out string why) &&
    liveBuild == "" && why == "NullReferenceException", "an uninitialized Steam Apps interface fails closed without a fake version");
Check(!LiveBuild.TryRead(true, () => 0, out liveBuild, out _) && liveBuild == "", "zero Steam build is rejected");
Check(!LiveBuild.TryRead(true, () => -1, out liveBuild, out _) && liveBuild == "", "negative Steam build is rejected");
Check(LiveBuild.TryRead(true, () => 123456, out liveBuild, out why) && liveBuild == "123456" && why == "ready",
    "ready Steam build is used unchanged for metadata and filtering");
var ad = new Advertisement(native);
Check(!ad.IsPublic && native.Calls.Count == 0, "default is OFF and does not change native visibility");
Check(ad.Publish("123", false, HostPhase.Waiting), "explicit public hosting succeeds");
Check(native.Calls.Count == 7 && native.Calls[0] == "version=123" &&
    native.Calls[1].StartsWith(PublicPolicy.PublicationKey + "=") &&
    Guid.TryParseExact(native.Calls[1].Split('=')[1], "N", out var firstPublication) && firstPublication != Guid.Empty &&
    string.Join("|", native.Calls.Skip(2)) == "ratgame.state=waiting|public|joinable=True|ratgame.joinable=1|ratgame.public=1",
    "public marker is published last, after Steam visibility and admission succeed");

foreach (string failedStep in new[] { "version=123", "public", "joinable=True" })
{
    var lobby = new FakeLobby { Fail = call => call == failedStep };
    Check(!new Advertisement(lobby).Publish("123", true, HostPhase.Waiting) && !lobby.Calls.Contains("ratgame.public=1"),
        "no public advertisement after failed " + failedStep);
}
var deniedMarker = new FakeLobby { Fail = call => call == "ratgame.public=1" };
var failedAd = new Advertisement(deniedMarker);
Check(!failedAd.Publish("123", true, HostPhase.Waiting) && !failedAd.IsPublic && deniedMarker.Calls.Contains("friends"),
    "failed marker publication restores native friends-only visibility");
var deniedPublication = new FakeLobby { Fail = call => call.StartsWith(PublicPolicy.PublicationKey + "=") };
Check(!new Advertisement(deniedPublication).Publish("123", false, HostPhase.Waiting) && !deniedPublication.Calls.Contains("public") &&
    !deniedPublication.Calls.Contains("ratgame.public=1"), "failed publication generation never opens public visibility or advertising");
var rotationLobby = new FakeLobby();
var rotationAd = new Advertisement(rotationLobby);
rotationAd.Publish("123", false, HostPhase.Waiting);
string oldPublication = rotationLobby.Data[PublicPolicy.PublicationKey];
rotationAd.Withdraw(false);
rotationAd.Publish("123", false, HostPhase.Waiting);
Check(oldPublication != rotationLobby.Data[PublicPolicy.PublicationKey], "republishing a lobby creates a different publication generation");
rotationLobby.Calls.Clear();
rotationLobby.Fail = call => call == "ratgame.public=0";
Check(!rotationAd.Publish("123", false, HostPhase.Waiting) && !rotationLobby.Calls.Contains("ratgame.public=1"),
    "republication refuses an unwithdrawn previous public period");

native.Calls.Clear();
Check(ad.PrepareNativeChange() && native.Calls[0] == "ratgame.public=0" && native.Calls[1] == "ratgame.joinable=0" && native.Calls[2] == "joinable=False",
    "native visibility transition withdraws before changing visibility");
Check(ad.Withdraw(false) && native.Calls.Contains("invisible") && !ad.NeedsCleanup,
    "native Private retains the existing Steam Invisible mapping");

var privacyFailure = new FakeLobby();
var cleanup = new Advertisement(privacyFailure);
cleanup.Publish("123", false, HostPhase.Waiting);
privacyFailure.Fail = call => call == "ratgame.public=0";
Check(!cleanup.Withdraw(false) && cleanup.NeedsCleanup && privacyFailure.Calls.Last() == "joinable=False",
    "failed marker withdrawal closes admission and retains cleanup work");
privacyFailure.Fail = _ => false;
Check(cleanup.Withdraw(true) && !cleanup.NeedsCleanup && privacyFailure.Calls.TakeLast(2).SequenceEqual(new[] { "friends", "joinable=True" }),
    "cleanup retries restore chosen native visibility before reopening admission");
privacyFailure.Fail = call => call == "ratgame.public=0";
cleanup.Withdraw(true);
privacyFailure.Fail = _ => false;
Check(cleanup.Publish("123", true, HostPhase.Waiting) && !cleanup.NeedsCleanup,
    "a later successful explicit publication clears an obsolete cleanup obligation");

var allDenied = new FakeLobby();
var unsafeChange = new Advertisement(allDenied);
unsafeChange.Publish("123", false, HostPhase.Waiting);
allDenied.Fail = _ => true;
Check(!unsafeChange.PrepareNativeChange() && unsafeChange.IsPublic,
    "native transition is refused when Steam refuses both withdrawal and closure");
var nonOwner = new FakeLobby { IsOwner = false };
Check(!new Advertisement(nonOwner).Publish("123", false, HostPhase.Waiting) && nonOwner.Calls.Count == 0,
    "non-owner cannot publish a lobby");

Check(PublicPolicy.IsCandidate(1, "1", "123", "123", 1, 10), "explicit compatible public lobby is discoverable");
foreach (var marker in new[] { "", "0", "true", " 1", "1 " })
    Check(!PublicPolicy.IsCandidate(1, marker, "123", "123", 1, 10), "unmarked/private lobby excluded: '" + marker + "'");
Check(!PublicPolicy.IsCandidate(1, "1", "124", "123", 1, 10), "different Steam build excluded");
Check(!PublicPolicy.IsCandidate(1, "1", "123", "123", 10, 10), "full lobby excluded before joining");
Check(PublicPolicy.IsCandidate(1, "1", "123", "123", 10, 10, alreadyJoined: true),
    "successful last-slot join counts the local member correctly");
Check(!PublicPolicy.IsCandidate(0, "1", "123", "123", 1, 10) &&
    !PublicPolicy.IsCandidate(1, "1", "123", "123", -1, 10) &&
    !PublicPolicy.IsCandidate(1, "1", "123", "123", 0, 0), "invalid ID and capacity information excluded");

var lease = new JoinLease(42);
Check(lease.SuppressEntry(42) && !lease.SuppressEntry(43), "only the selected public lobby entry is quarantined");
lease.AllowNativeEntry = true;
Check(!lease.SuppressEntry(42), "successful controlled join can hand off to the native handler");
lease.Cancelled = true;
Check(lease.SuppressEntry(42), "late callback remains suppressed after cancellation, even during handoff");
int starts = 0, rejected = 0;
lease = new JoinLease(42);
var deferred = lease.GuardDeferred(() => starts++, () => true, () => true, () => rejected++);
deferred();
lease.Cancelled = true;
new JoinLease(46) { Cancelled = true }.GuardDeferred(() => starts++, () => true, () => true, () => rejected++)();
Check(starts == 1 && rejected == 1, "cancelled public join cannot start a deferred Netcode connection");
new JoinLease(43).GuardDeferred(() => starts++, () => false, () => true, () => rejected++)();
Check(starts == 1 && rejected == 2, "replaced or disconnected public attempt cannot start later");
lease = new JoinLease(44) { MembershipIssued = true };
Check(lease.NeedsEntryQuarantine, "Task completion before Steam entry event retains an ID-specific quarantine");
lease.EntryObserved = true;
Check(!lease.NeedsEntryQuarantine, "an observed automatic entry event releases the callback quarantine");
Check(!new JoinLease(45).NeedsEntryQuarantine, "metadata-only failure does not quarantine unrelated native joins");

const string publicationOne = "11111111111111111111111111111111";
const string publicationTwo = "22222222222222222222222222222222";
PublicLobbySnapshot Snapshot(string marker = "1", string version = "123", string publication = publicationOne,
    int members = 1, int capacity = 10, ulong id = 42, ulong owner = 7, string state = "waiting", string joinable = "1") =>
    new(id, owner, marker, version, publication, members, capacity, state, joinable);
Check(PublicJoinScope.TryCapture(Snapshot(), "123", out var rowScope), "browser binds a row to a valid immutable publication and lobby identity");
foreach (string malformed in new[] { "", "not-a-generation", new string('0', 32) })
    Check(!PublicJoinScope.TryCapture(Snapshot(publication: malformed), "123", out _),
        "legacy or malformed publication rows are excluded: " + (malformed == "" ? "missing" : "invalid"));
Check(PublicJoinScope.TryCapture(Snapshot(owner: 0), "123", out var nonmemberScope),
    "nonmember discovery works without the member-only Steam owner getter");
Check(!PublicJoinScope.TryCapture(Snapshot(members: 0), "123", out _), "empty or unavailable hosted membership fails closed");
Check(rowScope!.Validate(Snapshot(marker: "0"), "123", false) == JoinRejection.NotPublic,
    "stale public row is rejected after public withdrawal");
Check(rowScope.Validate(Snapshot(publication: publicationTwo), "123", false) == JoinRejection.PublicationChanged,
    "a stale row cannot silently authorize a later public period of the same lobby");
Check(rowScope.Validate(Snapshot(id: 43), "123", false) == JoinRejection.IdentityChanged,
    "a different lobby invalidates the selected row");
var nonmember = new JoinLease(42, nonmemberScope);
Check(nonmember.Revalidate(Snapshot(owner: 0), "123", false), "pre-entry metadata revalidation does not require a lobby owner");
nonmember.MembershipAccepted = true;
Check(nonmember.Revalidate(Snapshot(owner: 7), "123", true), "owner is first pinned after legitimate Steam membership");
Check(!nonmember.Revalidate(Snapshot(owner: 8), "123", true) && nonmember.Rejection == JoinRejection.IdentityChanged,
    "owner changing after membership invalidates the deferred connection");
Check(!new JoinLease(42, nonmemberScope) { MembershipAccepted = true }.Revalidate(Snapshot(owner: 0), "123", true),
    "missing owner after actual membership fails closed");
Check(!new JoinLease(42, nonmemberScope) { MembershipAccepted = true }.Revalidate(Snapshot(owner: 9), "123", true, localUser: 9),
    "an abandoned lobby that elects the joining client owner never connects that client to itself");
Check(rowScope.Validate(Snapshot(version: "124"), "123", false) == JoinRejection.BuildMismatch &&
    rowScope.Validate(Snapshot(), "124", false) == JoinRejection.BuildMismatch &&
    rowScope.Validate(Snapshot(), "", false) == JoinRejection.BuildMismatch,
    "live build and lobby version are both rechecked at the final boundary");
Check(rowScope.Validate(Snapshot(members: 10), "123", false) == JoinRejection.Full &&
    rowScope.Validate(Snapshot(members: 10), "123", true) == JoinRejection.None &&
    rowScope.Validate(Snapshot(members: 11), "123", true) == JoinRejection.Full,
    "available slots checked before Steam entry; accepted final-slot membership remains valid");
Check(rowScope.Validate(Snapshot(capacity: 0), "123", true) == JoinRejection.InvalidCapacity,
    "lost capacity metadata invalidates a pending connection");

var pending = new JoinLease(42, rowScope);
var latest = Snapshot();
int nativeStarts = 0, deniedStarts = 0;
var guardedStart = pending.GuardDeferred(() => nativeStarts++, () => true,
    () => pending.Revalidate(latest, "123", true), () => deniedStarts++);
Check(pending.Revalidate(latest, "123", false), "fresh metadata authorizes the initial Steam membership request");
latest = Snapshot(marker: "0");
guardedStart();
Check(nativeStarts == 0 && deniedStarts == 1 && pending.Cancelled && pending.Rejection == JoinRejection.NotPublic,
    "withdrawal after membership but before deferred native action suppresses StartClient");
latest = Snapshot(publication: publicationTwo);
guardedStart();
Check(nativeStarts == 0 && pending.Rejection == JoinRejection.NotPublic,
    "observed revocation stays cancelled even if the host republishes before a delayed callback");
Check(PublicJoinScope.TryCapture(latest, "123", out var refreshedScope), "an explicit fresh browser refresh can acquire the new public period");
var reconnect = new JoinLease(42, refreshedScope);
var reconnectAction = reconnect.GuardDeferred(() => nativeStarts++, () => true,
    () => reconnect.Revalidate(latest, "123", true), () => deniedStarts++);
reconnectAction();
reconnectAction();
Check(nativeStarts == 1 && reconnect.DeferredStarted, "a valid reconnect starts the native action once despite repeated invocation");
var disconnected = new JoinLease(42, refreshedScope);
var oldReconnectAction = disconnected.GuardDeferred(() => nativeStarts++, () => false,
    () => disconnected.Revalidate(latest, "123", true), () => deniedStarts++);
oldReconnectAction();
Check(nativeStarts == 1 && disconnected.Cancelled && !reconnect.Cancelled,
    "a replaced or disconnected attempt cannot start or cancel a newer reconnect");
var changedWhileConnecting = new JoinLease(42, rowScope) { MembershipIssued = true };
Check(!changedWhileConnecting.Revalidate(Snapshot(marker: "0"), "123", true) &&
    !changedWhileConnecting.Revalidate(Snapshot(), "123", true),
    "metadata watcher revocation is irreversible while waiting for local connection confirmation");

var lane = new RequestLane<bool>();
var operation = new TaskCompletionSource<bool>();
var wait = lane.Run(() => operation.Task, TimeSpan.FromMilliseconds(30), CancellationToken.None);
bool overlapped = false;
try { _ = lane.Run(() => Task.FromResult(true), TimeSpan.FromSeconds(1), CancellationToken.None); }
catch (InvalidOperationException) { overlapped = true; }
Check(overlapped, "overlapping Steam requests are refused");
try { await wait; throw new Exception("Expected timeout"); }
catch (TimeoutException) { }
Check(lane.IsBusy, "timeout does not release a still-running Steam operation");
operation.SetResult(true);
for (int i = 0; i < 100 && lane.IsBusy; i++) await Task.Delay(2);
Check(!lane.IsBusy && await lane.Run(() => Task.FromResult(true), TimeSpan.FromSeconds(1), CancellationToken.None),
    "late completion releases the slot and permits a later refresh");

var cancellation = new CancellationTokenSource();
operation = new TaskCompletionSource<bool>();
wait = lane.Run(() => operation.Task, TimeSpan.FromSeconds(1), cancellation.Token);
cancellation.Cancel();
try { await wait; throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { }
Check(lane.IsBusy, "cancelling a wait also retains the underlying request slot");
operation.SetException(new Exception("late Steam failure"));
for (int i = 0; i < 100 && lane.IsBusy; i++) await Task.Delay(2);
Check(!lane.IsBusy, "late failed request is observed and releases its slot");
bool failed = false;
try { await lane.Run(() => Task.FromException<bool>(new Exception("request failed")), TimeSpan.FromSeconds(1), CancellationToken.None); }
catch (Exception error) when (error.Message == "request failed") { failed = true; }
Check(failed && !lane.IsBusy, "immediate failed request propagates without wedging future refreshes");
int refreshStarts = 0;
operation = new TaskCompletionSource<bool>();
wait = lane.Run(() => { refreshStarts++; return operation.Task; }, TimeSpan.FromSeconds(2), CancellationToken.None);
int refusedRefreshes = 0;
for (int i = 0; i < 20; i++)
{
    try { _ = lane.Run(() => { refreshStarts++; return Task.FromResult(true); }, TimeSpan.FromSeconds(1), CancellationToken.None); }
    catch (InvalidOperationException) { refusedRefreshes++; }
}
Check(refreshStarts == 1 && refusedRefreshes == 20, "twenty overlapping refreshes issue only one underlying Steam request");
operation.SetResult(true);
await wait;
for (int i = 0; i < 100 && lane.IsBusy; i++) await Task.Delay(2);
for (int i = 0; i < 20; i++) await lane.Run(() => { refreshStarts++; return Task.FromResult(true); }, TimeSpan.FromSeconds(1), CancellationToken.None);
Check(refreshStarts == 21 && !lane.IsBusy, "repeated sequential refreshes recover after the pending request settles");
using var cancelledRefresh = new CancellationTokenSource();
cancelledRefresh.Cancel();
try { await lane.Run(() => { refreshStarts++; return Task.FromResult(true); }, TimeSpan.FromSeconds(1), cancelledRefresh.Token); }
catch (OperationCanceledException) { }
Check(refreshStarts == 21 && !lane.IsBusy, "a cancelled refresh cannot start a new Steam request or hold its lane");
Check(UiPresentation.HostName(null) == "Unnamed host" && UiPresentation.HostName("\n\t") == "Unnamed host",
    "missing or control-only host labels have a useful fallback");
Check(UiPresentation.HostName("Chef\nRat\t") == "ChefRat" && UiPresentation.HostName(new string('x', 100)).Length == 80,
    "remote lobby labels cannot inject new lines or unbounded text");
Check(UiPresentation.RowAction(10, 10, true, false) == "Full" && UiPresentation.RowAction(11, 10, true, false) == "Full",
    "full or over-capacity rows cannot present Join");
Check(UiPresentation.RowAction(1, 10, false, false) == "Unavailable" && UiPresentation.RowAction(1, 10, true, true) == "Please wait",
    "withdrawn and pending rows cannot present Join");
Check(UiPresentation.Hosting(false, true, true).Contains("withdrawing") && UiPresentation.Hosting(true, false, false).Contains("next host"),
    "withdrawal and armed hosting are distinct from an advertised lobby");
foreach (string state in new[] { "", "unknown", "starting", "ingame", "Waiting", "waiting " })
{
    Check(!PublicJoinScope.TryCapture(Snapshot(state: state), "123", out _) &&
        rowScope.Validate(Snapshot(state: state), "123", false) == JoinRejection.NotWaiting,
        "cached public listing / deferred authorization rejects state '" + state + "'");
}
foreach (string joinable in new[] { "", "0", "true" })
    Check(!PublicJoinScope.TryCapture(Snapshot(joinable: joinable), "123", out _) &&
        rowScope.Validate(Snapshot(joinable: joinable), "123", true) == JoinRejection.NotJoinable,
        "public admission metadata must explicitly permit joining: '" + joinable + "'");
var transitionLease = new JoinLease(42, rowScope);
Check(!transitionLease.Revalidate(Snapshot(state: "starting"), "123", false) &&
    !transitionLease.Revalidate(Snapshot(), "123", false), "observed start revokes a pending join even after a rapid return to waiting");
foreach (var phase in new[] { HostPhase.Unknown, HostPhase.Starting, HostPhase.InGame })
{
    var closed = new FakeLobby();
    Check(!new Advertisement(closed).Publish("123", false, phase) && closed.Calls.Count == 0,
        "publication refuses lifecycle " + phase);
}
Check(HostPhasePolicy.Resolve(true, true, true, true, false, false, false, true, false) == HostPhase.Unknown,
    "native InLobby before scene completion fails closed");
Check(HostPhasePolicy.Resolve(true, true, true, true, false, false, true, false, false) == HostPhase.Unknown &&
    HostPhasePolicy.Resolve(true, true, true, true, false, false, true, true, true) == HostPhase.Unknown,
    "wrong scene or an active transition cannot advertise a waiting host");
Check(HostPhasePolicy.Resolve(true, true, true, true, false, false, true, true, false) == HostPhase.Waiting,
    "owned spawned host with a completed lobby scene can wait publicly");
Check(HostPhasePolicy.Resolve(true, true, true, false, true, false, true, true, false) == HostPhase.Starting &&
    HostPhasePolicy.Resolve(true, true, true, false, false, true, true, true, false) == HostPhase.InGame,
    "initializing, active play and end screens remain outside waiting");
Check(HostPhasePolicy.Resolve(false, true, true, true, false, false, true, true, false) == HostPhase.Unknown &&
    HostPhasePolicy.Resolve(true, false, true, true, false, false, true, true, false) == HostPhase.Unknown &&
    HostPhasePolicy.Resolve(true, true, false, true, false, false, true, true, false) == HostPhase.Unknown,
    "disconnect, ownership loss and an unspawned manager fail closed");
foreach (string failedStep in new[] { "ratgame.state=waiting", "ratgame.joinable=1" })
{
    var failedLifecycle = new FakeLobby { Fail = call => call == failedStep };
    Check(!new Advertisement(failedLifecycle).Publish("123", false, HostPhase.Waiting) && !failedLifecycle.Calls.Contains("ratgame.public=1"),
        "lifecycle/admission setter failure cannot publish: " + failedStep);
}
var matchLobby = new FakeLobby();
var matchAd = new Advertisement(matchLobby);
matchAd.Publish("123", false, HostPhase.Waiting);
string waitingGeneration = matchLobby.Data[PublicPolicy.PublicationKey];
matchLobby.Calls.Clear();
Check(matchAd.Withdraw(true, "starting") && matchLobby.Calls[0] == "ratgame.public=0" &&
    matchLobby.Data[PublicPolicy.JoinableKey] == "0" && matchLobby.Data[PublicPolicy.StateKey] == "starting" &&
    matchLobby.Calls.TakeLast(2).SequenceEqual(new[] { "friends", "joinable=True" }),
    "start withdrawal disables public joining while restoring native friends/invites");
Check(matchAd.Publish("123", true, HostPhase.Waiting) && matchLobby.Data[PublicPolicy.PublicationKey] != waitingGeneration,
    "a confirmed return to waiting creates a fresh period; stale rows cannot authorize it");

ModerationTests.Run(Check);
Console.WriteLine($"{passed} offline safety, presentation and moderation checks passed. No game, Unity, or Steam code was executed.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

sealed class FakeLobby : IHostLobby
{
    public bool IsOwner { get; set; } = true;
    public List<string> Calls { get; } = new();
    public Dictionary<string, string> Data { get; } = new();
    public Func<string, bool> Fail { get; set; } = _ => false;
    private bool Call(string value) { Calls.Add(value); return !Fail(value); }
    public bool SetData(string key, string value)
    {
        if (!Call(key + "=" + value)) return false;
        Data[key] = value;
        return true;
    }
    public bool SetPublic() => Call("public");
    public bool SetNative(bool friendsOnly) => Call(friendsOnly ? "friends" : "invisible");
    public bool SetJoinable(bool value) => Call("joinable=" + value);
}


