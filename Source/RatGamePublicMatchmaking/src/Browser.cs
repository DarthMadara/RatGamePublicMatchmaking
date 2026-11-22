using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private readonly RequestLane<Lobby[]?> publicQueries = new RequestLane<Lobby[]?>();
        private readonly RequestLane<bool> friendQueries = new RequestLane<bool>();
        private readonly List<Lobby> publicResults = new List<Lobby>();
        private readonly Dictionary<ulong, PublicJoinScope> publicScopes = new Dictionary<ulong, PublicJoinScope>();
        private Task? screenRefresh;
        private int browserEpoch;
        private float lastQueryAt = -10;
        private bool warnedUi;
        private bool publicRefreshInProgress;

        internal bool IsPublicHost => advertisement?.IsPublic == true && hostPhase == HostPhase.Waiting &&
            HostReady && hostedLobby?.IsOwner == true;
        internal void InvalidateBrowser() { screenRefresh = null; }

        internal Task RefreshScreen(FindLobbyScreen screen)
        {
            if (screenRefresh != null && !screenRefresh.IsCompleted) return screenRefresh;
            int epoch = browserEpoch;
            screenRefresh = RefreshScreenCore(screen, epoch);
            return screenRefresh;
        }

        private async Task RefreshScreenCore(FindLobbyScreen screen, int epoch)
        {
            if (screen.RefreshButton != null) screen.RefreshButton.ButtonEnabled = false;
            try
            {
                await Task.WhenAll(RefreshFriends(screen, epoch), RefreshPublic(null, epoch));
                if (IsCurrent(screen, epoch)) RenderPublicRows(screen);
            }
            finally
            {
                if (IsCurrent(screen, epoch) && screen.RefreshButton != null)
                    screen.RefreshButton.ButtonEnabled = true;
            }
        }

        private bool IsCurrent(FindLobbyScreen? screen, int epoch) => !shuttingDown &&
            epoch == browserEpoch && (screen == null || screen.gameObject.activeInHierarchy);

        private async Task RefreshFriends(FindLobbyScreen screen, int epoch)
        {
            friendsBrowserState = "Looking for friend lobbies...";
            try
            {
                var manager = SteamNetworkManager.instance;
                if (manager == null || !SteamClient.IsValid)
                    throw new InvalidOperationException("Steam is not ready. Try Refresh after Steam initializes.");
                bool success = await friendQueries.Run(manager.RefreshFriendLobbies, RequestTimeout, lifetime.Token);
                if (!IsCurrent(screen, epoch)) return;
                if (!success) throw new InvalidOperationException("The game's friend-lobby request failed.");
                if (screen.FriendLobbiesParent != null)
                {
                    ClearRows(screen.FriendLobbiesParent);
                    RenderMethod.Invoke(screen, new object[] { screen.FriendLobbiesParent, manager.FriendLobbies });
                }
                friendsBrowserState = manager.FriendLobbies.Count == 0 ? "No friend lobbies found." : "";
                UpdateEmptyLabel(screen);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (IsCurrent(screen, epoch))
                {
                    friendsBrowserState = "Could not refresh friends. Try Refresh.";
                    SetStatus("Friends refresh: " + error.Message);
                }
            }
        }

        private async Task RefreshPublic(FindLobbyScreen? screen, int epoch)
        {
            if (shuttingDown || publicRefreshInProgress || publicQueries.IsBusy || Time.unscaledTime - lastQueryAt < 2) return;
            publicRefreshInProgress = true;
            try
            {
                if (shuttingDown) return;
                lastQueryAt = Time.unscaledTime;
                if (!SteamClient.IsValid)
                    throw new InvalidOperationException("Steam is not ready. Try Refresh after Steam initializes.");
                publicBrowserState = "Looking for public lobbies...";
                publicResults.Clear();
                publicScopes.Clear();
                RenderAllPublicRows();
                if (!TryLiveBuild(out string build, "public-query"))
                    throw new InvalidOperationException("Steam build ID is not ready; no query with an unknown version was sent.");
                var results = await publicQueries.Run(() => new LobbyQuery()
                    .WithKeyValue(PublicPolicy.Marker, "1")
                    .WithKeyValue(PublicPolicy.StateKey, "waiting")
                    .WithKeyValue(PublicPolicy.JoinableKey, "1")
                    .WithKeyValue("version", build)
                    .WithSlotsAvailable(1)
                    .WithMaxResults(50)
                    .RequestAsync(), RequestTimeout, lifetime.Token);
                if (!IsCurrent(null, epoch)) return;
                if (!SteamClient.IsValid) throw new InvalidOperationException("Steam became unavailable during the query.");
                var seen = new HashSet<ulong>();
                foreach (var lobby in results ?? Array.Empty<Lobby>())
                {
                    // Validate returned metadata again; never fall back to an unfiltered query.
                    if (PublicJoinScope.TryCapture(ReadPublicSnapshot(lobby), build, out var scope) && seen.Add(lobby.Id))
                    { publicResults.Add(lobby); publicScopes.Add(lobby.Id, scope!); }
                }
                RenderAllPublicRows();
                publicBrowserState = publicResults.Count == 0 ? "No public lobbies returned. Refresh to try again, or host your own!" :
                    "Waiting lobbies - same build - refreshes every 30s";
                SetStatus("Found " + publicResults.Count + " explicitly public, compatible lobbies (nearby regions).");
                LogDiscoveryProbe(publicResults);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (IsCurrent(null, epoch))
                {
                    publicResults.Clear();
                    publicScopes.Clear();
                    publicBrowserState = "Could not refresh public lobbies. " + error.Message;
                    RenderAllPublicRows();
                    SetStatus("Public refresh: " + error.Message);
                    if (screen != null) UpdateEmptyLabel(screen);
                }
            }
            finally { publicRefreshInProgress = false; }
        }

        private void RenderAllPublicRows()
        {
            try
            {
                RenderTitleRows();
                if (browserUi != null && browserUi.gameObject.activeInHierarchy && HasPublicParent(browserUi)) RenderPublicRows(browserUi);
            }
            catch (Exception error)
            {
                Logger.LogWarning("Public rows unavailable; F8 remains usable: " + error.Message);
                titlePublicRows.Clear(); nativePublicRows.Clear();
                if (titleScroll != null) ClearRows(titleScroll.content);
                if (browserUi != null && HasPublicParent(browserUi)) ClearRows(browserUi.PublicLobbiesParent);
            }
        }

        private static PublicLobbySnapshot ReadPublicSnapshot(Lobby lobby, bool includeMemberOwner = false) => new PublicLobbySnapshot(
            lobby.Id, includeMemberOwner ? (ulong)lobby.Owner.Id : 0, lobby.GetData(PublicPolicy.Marker), lobby.GetData("version"),
            lobby.GetData(PublicPolicy.PublicationKey), lobby.MemberCount, lobby.MaxMembers,
            lobby.GetData(PublicPolicy.StateKey), lobby.GetData(PublicPolicy.JoinableKey));

        private static bool Candidate(Lobby lobby, string build) =>
            PublicJoinScope.TryCapture(ReadPublicSnapshot(lobby), build, out _);

        private static bool HasPublicParent(FindLobbyScreen screen)
        {
            var target = screen.PublicLobbiesParent;
            var friends = screen.FriendLobbiesParent;
            return target != null && target.gameObject.activeInHierarchy && target != friends &&
                (friends == null || (!target.IsChildOf(friends) && !friends.IsChildOf(target)));
        }

        private void RenderPublicRows(FindLobbyScreen screen)
        {
            if (!HasPublicParent(screen))
            {
                if (!warnedUi)
                {
                    warnedUi = true;
                    Logger.LogWarning("PublicLobbiesParent is absent, hidden, or shared. Use the F8 panel browser.");
                }
                return;
            }
            var parent = screen.PublicLobbiesParent;
            nativePublicRows.Clear();
            ClearRows(parent);
            RenderMethod.Invoke(screen, new object[] { parent, publicResults });
            var rows = parent.GetComponentsInChildren<LobbyElement>(true);
            if (rows.Length != publicResults.Count)
            {
                ClearRows(parent);
                throw new InvalidOperationException("Lobby prefab layout changed; use the F8 panel browser.");
            }
            for (int i = 0; i < rows.Length; i++)
            {
                var button = rows[i].JoinButton;
                if (button == null)
                {
                    ClearRows(parent);
                    throw new InvalidOperationException("Lobby prefab has no JoinButton; use the F8 panel.");
                }
                var lobby = publicResults[i];
                var scope = publicScopes[lobby.Id];
                // Only fresh public rows are changed. Native friend-row handlers remain untouched.
                button.ResetEvents();
                button.Clicked += () => BeginPublicJoin(lobby, scope);
                var count = StylePublicRow(rows[i]);
                nativePublicRows.Add(new PublicRow { Element = rows[i], PlayerCount = count, Lobby = lobby });
            }
            UpdatePublicRowStates();
            RebuildNavigation(screen);
        }

        private static TMPro.TMP_Text StylePublicRow(LobbyElement row)
        {
            foreach (var loc in row.JoinButton.GetComponentsInChildren<MonoBehaviour>(true))
                if (loc.GetType().Name == "LocSetter") { loc.enabled = false; Destroy(loc); }
            var layout = row.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            if (layout != null) layout.childForceExpandWidth = false;
            row.LobbyNameText.richText = false;
            row.LobbyNameText.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            row.LobbyNameText.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            row.LobbyNameText.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            var hostSize = row.LobbyNameText.GetComponent<UnityEngine.UI.LayoutElement>() ?? row.LobbyNameText.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            hostSize.minWidth = hostSize.preferredWidth = 0;
            hostSize.flexibleWidth = 1;
            var count = Instantiate(row.LobbyNameText, row.transform, false);
            count.name = "RGPM_PlayerCount";
            count.alignment = TMPro.TextAlignmentOptions.Center;
            count.raycastTarget = false;
            var countSize = count.GetComponent<UnityEngine.UI.LayoutElement>();
            countSize.minWidth = countSize.preferredWidth = 85;
            countSize.flexibleWidth = 0;
            count.transform.SetSiblingIndex(row.JoinButton.transform.GetSiblingIndex());
            var joinSize = row.JoinButton.GetComponent<UnityEngine.UI.LayoutElement>() ?? row.JoinButton.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            joinSize.minWidth = 120;
            joinSize.flexibleWidth = 0;
            return count;
        }

        private void UpdateEmptyLabel(FindLobbyScreen screen)
        {
            if (screen.NoLobbiesText != null)
                screen.NoLobbiesText.gameObject.SetActive(screen != browserUi && publicResults.Count == 0 &&
                    (SteamNetworkManager.instance == null || SteamNetworkManager.instance.FriendLobbies.Count == 0));
            RebuildNavigation(screen);
        }

        private static void ClearRows(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                child.gameObject.SetActive(false);
                child.SetParent(null, false);
                Destroy(child.gameObject);
            }
        }
    }
}
