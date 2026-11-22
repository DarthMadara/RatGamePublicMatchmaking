using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> nativeUiTools = null!;
        private readonly List<GameObject> uiAdditions = new List<GameObject>();
        private readonly Dictionary<GameObject, bool> originalUiActive = new Dictionary<GameObject, bool>();
        private readonly List<PublicRow> nativePublicRows = new List<PublicRow>();
        private StartScreen? titleUi;
        private LobbySettingsScreen? settingsUi;
        private FindLobbyScreen? browserUi;
        private PauseScreen? pauseUi;
        private GameScreen? gameUi;
        private CustomButton? titleHosting, settingsHosting, pauseHosting, cancelJoin;
        private TMP_Text? publicStateText, friendsStateText, hostingHint, publicBadge;
        private float nativeUiPollAt;
        private bool browserWasActive;
        private SokScreen? navigationScreen;
        private string publicBrowserState = "Refresh to find public lobbies.";
        private string friendsBrowserState = "Refresh to find friend lobbies.";

        private sealed class PublicRow
        {
            internal LobbyElement Element = null!;
            internal TMP_Text PlayerCount = null!;
            internal Steamworks.Data.Lobby Lobby;
        }

        private void PollNativeUi()
        {
            if (shuttingDown || Time.unscaledTime < nativeUiPollAt || GameCanvas.instance == null) return;
            nativeUiPollAt = Time.unscaledTime + 0.2f;
            try
            {
                var canvas = GameCanvas.instance;
                if (canvas.CurrentScreen != null && canvas.CurrentScreen != navigationScreen)
                {
                    navigationScreen = canvas.CurrentScreen;
                    RebuildNavigation(navigationScreen);
                }
                var title = canvas.GetScreen<StartScreen>();
                if (title != null && title != titleUi) InstallTitleUi(title);
                var settings = canvas.GetScreen<LobbySettingsScreen>();
                if (settings != null && settings != settingsUi) InstallHostingUi(settings);
                var browser = canvas.GetScreen<FindLobbyScreen>();
                if (browser != null && browser != browserUi) InstallBrowserUi(browser);
                var pause = canvas.GetScreen<PauseScreen>();
                if (pause != null && pause != pauseUi && pause.SettingsButton != null)
                {
                    pauseUi = pause;
                    pauseHosting = CopyButton(pause.SettingsButton, pause.SettingsButton.transform.parent, "PublicHosting", TogglePublicHosting);
                    pauseHosting.transform.SetSiblingIndex(pause.SettingsButton.transform.GetSiblingIndex());
                    RebuildNavigation(pause);
                }
                var game = canvas.GetScreen<GameScreen>();
                if (game != null && game != gameUi && titleUi != null)
                {
                    gameUi = game;
                    var badge = CopyButton(titleUi.HostButton, game.transform, "PublicAdvertisementBadge", () => { });
                    badge.enabled = false;
                    badge.ButtonEnabled = false;
                    badge.interactable = false;
                    badge.SelectableWithController = false;
                    badge.navigation = new Navigation { mode = Navigation.Mode.None };
                    badge.Image.raycastTarget = false;
                    badge.TextMeshPro.raycastTarget = false;
                    badge.Image.color = titleUi.HostButton.Image.color;
                    publicBadge = badge.TextMeshPro;
                    publicBadge.text = "PUBLIC LOBBY - ADVERTISED";
                    publicBadge.fontSize = 22;
                    var rect = (RectTransform)badge.transform;
                    rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 1);
                    rect.anchoredPosition = new Vector2(0, -20);
                    var fitter = badge.GetComponent<ContentSizeFitter>() ?? badge.gameObject.AddComponent<ContentSizeFitter>();
                    fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                }
                bool canToggle = SteamClient.IsValid && (HostReady || CanJoin) && pendingJoin == null;
                string hosting = UiPresentation.Hosting(publicHosting.Value, IsPublicHost, advertisement?.NeedsCleanup == true);
                bool hostingNavigationChanged = false;
                foreach (var button in new[] { titleHosting, settingsHosting, pauseHosting })
                {
                    if (button == null) continue;
                    button.TextMeshPro.text = hosting;
                    button.TooltipText = !SteamClient.IsValid ? "Steam is not ready. Public Hosting is unavailable." :
                        pendingJoin != null ? "Wait for the current public join to settle." :
                        "List your hosted lobby for everyone. Either native visibility arrow switches this off.";
                    hostingNavigationChanged |= button.ButtonEnabled != canToggle;
                    EnableButton(button, canToggle);
                }
                if (hostingNavigationChanged)
                {
                    if (titleUi != null) RebuildNavigation(titleUi);
                    if (settingsUi != null) RebuildNavigation(settingsUi);
                    if (pauseUi != null) RebuildNavigation(pauseUi);
                }
                if (hostingHint != null)
                    hostingHint.text = !SteamClient.IsValid ? "Steam is not ready. Public Hosting is unavailable." :
                        advertisement?.NeedsCleanup == true ? "Withdrawing public listing. Please wait." :
                        IsPublicHost ? "Anyone with your game build can find this lobby." :
                        publicHosting.Value ? "Public listing resumes when your host is waiting in the lobby." :
                        "Off keeps the Friends Only / Private selection above.";
                if (publicBadge != null) publicBadge.transform.parent.gameObject.SetActive(IsPublicHost || advertisement?.NeedsCleanup == true);
                if (publicBadge != null) publicBadge.text = advertisement?.NeedsCleanup == true ? "PUBLIC LISTING - WITHDRAWING" : "PUBLIC LOBBY - ADVERTISED";

                bool active = browserUi != null && browserUi.gameObject.activeInHierarchy && canvas.CurrentScreen == browserUi;
                if (active)
                {
                    if (publicStateText != null) publicStateText.text = pendingJoin != null && !pendingJoin.ConnectionConfirmed ? status : publicBrowserState;
                    if (friendsStateText != null) friendsStateText.text = friendsBrowserState;
                    if (browserUi!.NoLobbiesText != null) browserUi.NoLobbiesText.gameObject.SetActive(false);
                    if (cancelJoin != null)
                    {
                        bool visible = pendingJoin != null && !pendingJoin.ConnectionConfirmed;
                        if (cancelJoin.gameObject.activeSelf != visible)
                        {
                            cancelJoin.gameObject.SetActive(visible);
                            RebuildNavigation(browserUi);
                        }
                    }
                    UpdatePublicRowStates();
                    if (!browserWasActive) RebuildNavigation(browserUi);
                }
                browserWasActive = active;
                PollTitleBrowser();
                PollUiCommands();
            }
            catch (Exception error)
            {
                // Fail UI additions independently of the existing network/update guards.
                Logger.LogWarning("Native UI unavailable: " + error.GetType().Name + ": " + error.Message);
                nativeUiPollAt = Time.unscaledTime + 5;
            }
        }

        private void TogglePublicHosting()
        {
            if (!SteamClient.IsValid || (!HostReady && !CanJoin) || pendingJoin != null) return;
            publicHosting.Value = !publicHosting.Value;
            if (advertisement == null)
                SetStatus(publicHosting.Value ? "Public Hosting is armed for your next hosted lobby." : "Public Hosting is OFF.");
        }

        private void InstallTitleUi(StartScreen screen)
        {
            if (screen.HostButton == null || screen.HostButton.TextMeshPro == null) return;
            titleUi = screen;
            titleHosting = CopyButton(screen.HostButton, screen.HostButton.transform.parent, "PublicHosting", TogglePublicHosting);
            titleHosting.transform.SetSiblingIndex(screen.HostButton.transform.GetSiblingIndex() + 1);
            titleHosting.TextMeshPro.fontSize = 24;
            titleHosting.TooltipText = "Off by default. Turn on to list the lobby for everyone when you host.";
            RebuildNavigation(screen);
        }

        private void InstallHostingUi(LobbySettingsScreen screen)
        {
            var horizontal = screen.LobbyVisibilityText == null ? null : screen.LobbyVisibilityText.transform.parent;
            var row = horizontal == null ? null : horizontal.parent;
            if (row == null || horizontal != VisibilityRow(screen) || row.parent == null ||
                row.GetComponent<VerticalLayoutGroup>() == null || screen.PlayButton == null) return;
            settingsUi = screen;
            // Verified from the live scene: the value and both arrows share this vertical tabber.
            settingsHosting = CopyButton(screen.PlayButton, row.parent, "PublicHosting", TogglePublicHosting);
            settingsHosting.transform.SetSiblingIndex(row.GetSiblingIndex() + 1);
            settingsHosting.TextMeshPro.fontSize = 26;
            settingsHosting.TooltipText = "Public Hosting advertises this lobby. Either native visibility arrow switches it off.";
            hostingHint = CopyText(screen.LobbyVisibilityText!, row.parent, "PublicHostingHint", 21);
            hostingHint.transform.SetSiblingIndex(settingsHosting.transform.GetSiblingIndex() + 1);
            hostingHint.alignment = TextAlignmentOptions.Center;
            var hintLayout = hostingHint.GetComponent<LayoutElement>() ?? hostingHint.gameObject.AddComponent<LayoutElement>();
            hintLayout.preferredHeight = 48;
            RebuildNavigation(screen);
        }

        private void InstallBrowserUi(FindLobbyScreen screen)
        {
            var parent = screen.PublicLobbiesParent;
            var friends = screen.FriendLobbiesParent;
            if (parent == null || friends == null || parent == friends || parent.parent != friends.parent ||
                parent.GetComponent<VerticalLayoutGroup>() == null || screen.RefreshButton == null) return;
            // Resolve the heading by adjacency and component type after checking serialized references.
            int index = parent.GetSiblingIndex();
            if (index < 1) return;
            var heading = parent.parent.GetChild(index - 1).GetComponent<TMP_Text>();
            var friendsHeading = friends.GetSiblingIndex() < 1 ? null : friends.parent.GetChild(friends.GetSiblingIndex() - 1).GetComponent<TMP_Text>();
            if (heading == null || friendsHeading == null) return;
            browserUi = screen;
            RememberActive(parent.gameObject);
            RememberActive(heading.gameObject);
            parent.gameObject.SetActive(true);
            heading.gameObject.SetActive(true);
            publicStateText = CopyText(friendsHeading, parent.parent, "PublicLobbyStatus", 23);
            publicStateText.transform.SetSiblingIndex(parent.GetSiblingIndex());
            friendsStateText = CopyText(friendsHeading, friends.parent, "FriendLobbyStatus", 23);
            friendsStateText.transform.SetSiblingIndex(friends.GetSiblingIndex());
            if (screen.NoLobbiesText != null) RememberActive(screen.NoLobbiesText.gameObject);
            cancelJoin = CopyButton(screen.RefreshButton, screen.RefreshButton.transform.parent, "CancelPublicJoin", CancelPublicJoin);
            cancelJoin.TextMeshPro.text = "Cancel public join";
            cancelJoin.transform.SetSiblingIndex(screen.RefreshButton.transform.GetSiblingIndex());
            cancelJoin.gameObject.SetActive(false);
            RebuildNavigation(screen);
            Logger.LogInfo("Native Find Lobby public section enabled with original row prefab and fonts.");
        }

        private CustomButton CopyButton(CustomButton source, Transform parent, string name, Action clicked)
        {
            var button = Instantiate(source, parent, false);
            button.gameObject.name = "RGPM_" + name;
            uiAdditions.Add(button.gameObject);
            button.ResetEvents();
            button.Clicked += clicked;
            button.SelectableWithController = true;
            button.Hovered = false;
            button.TargetScale = Vector3.one;
            button.transform.localScale = Vector3.one;
            button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            var layout = button.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = false;
            foreach (var loc in button.GetComponentsInChildren<MonoBehaviour>(true))
                if (loc.GetType().Name == "LocSetter") { loc.enabled = false; Destroy(loc); }
            button.TextMeshPro.richText = false;
            button.gameObject.SetActive(true);
            return button;
        }

        private TMP_Text CopyText(TMP_Text source, Transform parent, string name, float size)
        {
            var text = Instantiate(source, parent, false);
            text.gameObject.name = "RGPM_" + name;
            uiAdditions.Add(text.gameObject);
            foreach (var loc in text.GetComponents<MonoBehaviour>())
                if (loc.GetType().Name == "LocSetter") { loc.enabled = false; Destroy(loc); }
            text.text = "";
            text.fontSize = size;
            text.richText = false;
            text.raycastTarget = false;
            text.color = Color.Lerp(source.color, Color.gray, 0.65f);
            text.gameObject.SetActive(true);
            return text;
        }

        private static void EnableButton(CustomButton button, bool value)
        {
            button.ButtonEnabled = value;
            button.interactable = value;
            if (button.TextMeshPro != null) button.TextMeshPro.alpha = value ? 1 : 0.5f;
        }

        private static void RebuildNavigation(SokScreen screen)
        {
            // Native navigation uses world positions; settle layout before deriving links.
            Canvas.ForceUpdateCanvases();
            screen.FixNavigation();
        }

        private void UpdatePublicRowStates()
        {
            bool buildReady = TryLiveBuild(out string build, "native-browser-display");
            bool navigationChanged = false;
            var allRows = new List<PublicRow>(nativePublicRows);
            allRows.AddRange(titlePublicRows);
            foreach (var entry in allRows)
            {
                if (entry.Element == null) continue;
                var button = entry.Element.JoinButton;
                var lobby = entry.Lobby;
                bool available = buildReady && publicScopes.TryGetValue(lobby.Id, out var scope) &&
                    scope.Validate(ReadPublicSnapshot(lobby), build, false) == JoinRejection.None;
                string action = UiPresentation.RowAction(lobby.MemberCount, lobby.MaxMembers, available, pendingJoin != null);
                bool enabledNow = action == "Join" && CanJoin && pendingJoin == null;
                navigationChanged |= button.ButtonEnabled != enabledNow;
                EnableButton(button, enabledNow);
                button.TextMeshPro.text = action;
                entry.Element.LobbyNameText.text = UiPresentation.HostName(lobby.GetData("name"));
                entry.PlayerCount.text = lobby.MemberCount + "/" + lobby.MaxMembers;
            }
            if (navigationChanged && browserUi != null) RebuildNavigation(browserUi);
            if (navigationChanged && titleUi != null) RebuildNavigation(titleUi);
        }

        private void RememberActive(GameObject target)
        {
            if (!originalUiActive.ContainsKey(target)) originalUiActive.Add(target, target.activeSelf);
        }

        private void RestoreNativeUi()
        {
            RestoreTooltipRotation();
            if (uiTestGamepad != null) { UnityEngine.InputSystem.InputSystem.RemoveDevice(uiTestGamepad); uiTestGamepad = null; }
            foreach (var target in uiAdditions) if (target != null) { target.SetActive(false); Destroy(target); }
            uiAdditions.Clear();
            foreach (var entry in originalUiActive) if (entry.Key != null) entry.Key.SetActive(entry.Value);
            originalUiActive.Clear();
        }
    }
}
