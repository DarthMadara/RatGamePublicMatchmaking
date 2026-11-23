using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RatGamePublicMatchmaking
{
    // Registered with the game's own screen manager: native cursor/input/menu routing.
    public sealed class ModerationScreen : SokScreen
    {
        internal Action? Cancel;
        public override void LeaveScreen() { Cancel = null; base.LeaveScreen(); }
        protected override void Update()
        {
            base.Update();
            if (InputController.instance != null && (InputController.instance.CancelTriggered() || InputController.instance.GetPauseTriggered()))
                Cancel?.Invoke();
        }
    }

    public sealed partial class Plugin
    {
        private CustomButton? managePlayersButton, manageBansButton;
        private ModerationScreen? moderationScreen;
        private SokScreen? moderationReturn;
        private RectTransform? moderationPaper;
        private ScrollRect? moderationScroll;
        private TMP_Text? moderationHeading, moderationInfo;
        private CustomButton? moderationBack, moderationPlayersTab, moderationBansTab;
        private CustomButton? kickButton, banButton, confirmButton, cancelBanButton, previousBans, nextBans;
        private ModerationPeer? selectedPlayer, confirmBan;
        private bool showBans;
        private int banPage;
        private string rosterSignature = "";
        private float nextModerationUiPoll;
        private const int BansPerPage = 20;

        private void PollModerationUi()
        {
            if (GameCanvas.instance == null) return;
            if (managePlayersButton == null && pauseUi != null && pauseUi.SettingsButton != null)
            {
                managePlayersButton = CopyButton(pauseUi.SettingsButton, pauseUi.SettingsButton.transform.parent,
                    "ManagePlayers", () => OpenModeration(false));
                managePlayersButton.TextMeshPro.text = "Manage Players";
                managePlayersButton.transform.SetSiblingIndex(pauseUi.SettingsButton.transform.GetSiblingIndex());
                manageBansButton = CopyButton(pauseUi.SettingsButton, pauseUi.SettingsButton.transform.parent,
                    "ManageBans", () => OpenModeration(true));
                manageBansButton.TextMeshPro.text = "Manage Bans";
                manageBansButton.transform.SetSiblingIndex(pauseUi.SettingsButton.transform.GetSiblingIndex());
            }
            bool authority = ModerationAuthority;
            foreach (var button in new[] { managePlayersButton, manageBansButton })
            {
                if (button == null || button.gameObject.activeSelf == authority) continue;
                button.gameObject.SetActive(authority);
                if (pauseUi != null) RebuildNavigation(pauseUi);
            }
            if (moderationScreen == null || GameCanvas.instance.CurrentScreen != moderationScreen) return;
            if (!authority) { CloseModerationUi(); return; }
            LayoutModeration();
            if (Time.unscaledTime < nextModerationUiPoll) return;
            nextModerationUiPoll = Time.unscaledTime + 0.25f;
            RefreshModerationRows();
            KeepModerationSelectionVisible();
        }

        private void OpenModeration(bool bansPage)
        {
            if (!ModerationAuthority || GameCanvas.instance == null) return;
            if (moderationScreen == null)
            {
                try { if (!InstallModerationScreen()) { ModerationUiUnavailable(); return; } }
                catch (Exception error)
                {
                    if (moderationScreen != null)
                    {
                        RemoveModerationUi();
                        uiAdditions.Remove(moderationScreen.gameObject);
                        Destroy(moderationScreen.gameObject);
                        moderationScreen = null;
                    }
                    ModerationUiUnavailable();
                    Logger.LogWarning("Native moderation UI construction failed: " + error.GetType().Name);
                    return;
                }
            }
            moderationReturn = GameCanvas.instance.CurrentScreen;
            showBans = bansPage; banPage = 0;
            selectedPlayer = confirmBan = null;
            rosterSignature = "";
            moderationStatus = bans.Error.Length == 0 ? "Select a player to manage." : bans.Error;
            GameCanvas.instance.SetScreen(moderationScreen!);
            ApplyModerationButtonLabels();
            moderationScreen!.Cancel = CloseModerationUi;
            RefreshModerationRows();
            EventSystem.current?.SetSelectedGameObject(moderationBack!.gameObject);
        }

        private void ApplyModerationButtonLabels()
        {
            // The screen is built inactive. Activating it runs the cloned native
            // LocSetter.Awake before its deferred Destroy, overwriting our labels.
            // Apply this screen's text after activation; native localization stays scoped.
            moderationPlayersTab!.TextMeshPro.text = "Players";
            moderationBansTab!.TextMeshPro.text = "Bans";
            kickButton!.TextMeshPro.text = "Kick";
            banButton!.TextMeshPro.text = "Ban...";
            confirmButton!.TextMeshPro.text = "Confirm ban";
            cancelBanButton!.TextMeshPro.text = "Cancel ban";
            previousBans!.TextMeshPro.text = "Previous";
            nextBans!.TextMeshPro.text = "Next";
            moderationBack!.TextMeshPro.text = "Back";
        }

        private void ModerationUiUnavailable()
        {
            moderationStatus = "Native player management UI is unavailable in this game build.";
            foreach (var button in new[] { managePlayersButton, manageBansButton })
                if (button != null) button.TooltipText = moderationStatus;
            Logger.LogWarning(moderationStatus);
        }

        private bool InstallModerationScreen()
        {
            if (browserUi == null || pauseUi == null || browserUi.RefreshButton == null) return false;
            var paper = browserUi.transform.Find("Box")?.GetComponent<Image>();
            var sourceScroll = browserUi.PublicLobbiesParent.GetComponentInParent<ScrollRect>(true);
            if (paper == null || sourceScroll == null || sourceScroll.content == null || sourceScroll.viewport == null) return false;
            var root = new GameObject("RGPM_Moderation", typeof(RectTransform));
            root.SetActive(false);
            root.transform.SetParent(pauseUi.transform.parent, false);
            var rootRect = (RectTransform)root.transform;
            rootRect.anchorMin = Vector2.zero; rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
            moderationScreen = root.AddComponent<ModerationScreen>();
            GameCanvas.instance.Screens.Add(moderationScreen);
            uiAdditions.Add(root);

            moderationPaper = new GameObject("Paper", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<RectTransform>();
            moderationPaper.SetParent(root.transform, false);
            var image = moderationPaper.GetComponent<Image>();
            image.sprite = paper.sprite; image.type = paper.type; image.color = paper.color; image.material = paper.material;
            var nativeShadow = paper.GetComponent<Shadow>();
            if (nativeShadow != null)
            {
                var shadow = image.gameObject.AddComponent<Shadow>();
                shadow.effectColor = nativeShadow.effectColor; shadow.effectDistance = nativeShadow.effectDistance;
            }
            moderationHeading = CopyText(browserUi.RefreshButton.TextMeshPro, moderationPaper, "ModerationHeading", 34);
            moderationHeading.color = browserUi.RefreshButton.TextMeshPro.color;
            moderationHeading.alignment = TextAlignmentOptions.Center;
            Stretch(moderationHeading.rectTransform, 30, 30, 24, 0, 46);
            moderationPlayersTab = ModerationButton("PlayersTab", "Players", () => ChangeModerationPage(false), -210, 316, 300);
            moderationBansTab = ModerationButton("BansTab", "Bans", () => ChangeModerationPage(true), 210, 316, 300);

            moderationScroll = Instantiate(sourceScroll, moderationPaper, false);
            moderationScroll.name = "RGPM_ModerationScroll";
            Stretch((RectTransform)moderationScroll.transform, 28, 28, 160, 260);
            foreach (Transform child in moderationScroll.viewport)
                if (child != moderationScroll.content) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            ClearRows(moderationScroll.content);
            moderationScroll.horizontal = false; moderationScroll.vertical = true;
            moderationScroll.movementType = ScrollRect.MovementType.Clamped;
            var content = moderationScroll.content;
            content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>() ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 8, 8); layout.spacing = 10;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            var fit = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained; fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            moderationInfo = CopyText(browserUi.RefreshButton.TextMeshPro, moderationPaper, "ModerationInfo", 22);
            moderationInfo.alignment = TextAlignmentOptions.TopLeft;
            moderationInfo.textWrappingMode = TextWrappingModes.Normal;
            Stretch(moderationInfo.rectTransform, 40, 40, 632, 0, 75);
            kickButton = ModerationButton("Kick", "Kick", KickSelected, -210, -334, 270);
            banButton = ModerationButton("Ban", "Ban...", BeginBanSelected, 210, -334, 270);
            // Original Ban position becomes Cancel: a double click cannot confirm.
            confirmButton = ModerationButton("ConfirmBan", "Confirm ban", ConfirmSelectedBan, -210, -334, 300);
            cancelBanButton = ModerationButton("CancelBan", "Cancel ban", () => { confirmBan = null; RefreshModerationRows(); }, 210, -334, 300);
            previousBans = ModerationButton("PreviousBans", "Previous", () => { banPage--; rosterSignature = ""; RefreshModerationRows(); }, -280, -334, 240);
            nextBans = ModerationButton("NextBans", "Next", () => { banPage++; rosterSignature = ""; RefreshModerationRows(); }, 280, -334, 240);
            moderationBack = ModerationButton("ModerationBack", "Back", CloseModerationUi, 0, -396, 220);
            LayoutModeration();
            return true;
        }

        private CustomButton ModerationButton(string id, string label, Action action, float x, float y, float width)
        {
            var button = CopyButton(browserUi!.RefreshButton, moderationPaper!, id, action);
            button.TextMeshPro.text = label;
            var rect = (RectTransform)button.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(width, 52);
            var fit = button.GetComponent<ContentSizeFitter>();
            if (fit != null) fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            var layout = button.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = true;
            return button;
        }

        private void LayoutModeration()
        {
            if (moderationPaper == null || moderationScreen == null) return;
            var parent = (RectTransform)moderationScreen.transform;
            var canvas = GameCanvas.instance.MyCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, Screen.safeArea.min, camera, out var min);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, Screen.safeArea.max, camera, out var max);
            moderationPaper.anchorMin = moderationPaper.anchorMax = moderationPaper.pivot = new Vector2(0.5f, 0.5f);
            moderationPaper.sizeDelta = new Vector2(1000, 880);
            moderationPaper.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1, (max.x - min.x - 48) / 1000, (max.y - min.y - 48) / 880));
            moderationPaper.anchoredPosition = (min + max) / 2;
        }

        private void ChangeModerationPage(bool bansPage)
        {
            if (!ModerationAuthority) return;
            showBans = bansPage; banPage = 0; selectedPlayer = confirmBan = null; rosterSignature = "";
            RefreshModerationRows();
        }

        private void RefreshModerationRows()
        {
            if (!ModerationAuthority || moderationScroll == null || moderationScreen == null) return;
            var peers = new List<ModerationPeer>();
            var unresolved = new List<ulong>();
            foreach (ulong id in NetworkManager.Singleton.ConnectedClientsIds.ToArray())
            {
                if (id == moderationSession.HostClientId) continue;
                if (moderationSession.Resolve(id, out var peer)) peers.Add(peer); else unresolved.Add(id);
            }
            if (selectedPlayer.HasValue && !peers.Any(p => p.Equals(selectedPlayer.Value)))
            { selectedPlayer = confirmBan = null; moderationStatus = "Selected player disconnected. Select another player."; }
            if (confirmBan.HasValue && (!selectedPlayer.HasValue || !confirmBan.Value.Equals(selectedPlayer.Value))) confirmBan = null;
            var banned = bans.Snapshot();
            int pages = Math.Max(1, (banned.Length + BansPerPage - 1) / BansPerPage);
            banPage = Math.Max(0, Math.Min(banPage, pages - 1));
            moderationHeading!.text = showBans ? "Manage Bans - " + (banPage + 1) + "/" + pages : "Manage Players";
            string signature = showBans + ":" + banPage + ":" + selectedPlayer?.Generation + ":" + string.Join("|",
                peers.Select(p => p.ClientId + ":" + p.TransportId + ":" + p.Generation + ":" + p.Name)) + ":" +
                string.Join(",", unresolved) + ":" + string.Join("|", banned.Select(p => p.Key + ":" + p.Value));
            if (signature != rosterSignature)
            {
                rosterSignature = signature;
                bool wasInRows = EventSystem.current?.currentSelectedGameObject != null &&
                    EventSystem.current.currentSelectedGameObject.transform.IsChildOf(moderationScroll.content);
                ClearRows(moderationScroll.content);
                if (showBans)
                {
                    foreach (var entry in banned.Skip(banPage * BansPerPage).Take(BansPerPage))
                        ModerationRow(entry.Value + " (" + entry.Key + ") - Unban", () =>
                        {
                            if (!ModerationAuthority) return;
                            moderationStatus = moderator.Unban(entry.Key) ? "Account unbanned; they may reconnect." : bans.Error;
                            rosterSignature = ""; RefreshModerationRows();
                        }, bans.Ready);
                    if (banned.Length == 0) ModerationRow("No banned accounts.", () => { }, false);
                }
                else
                {
                    ModerationRow(SteamDisplayName(moderationSession.HostSteamId) + " [Host]", () => { }, false);
                    foreach (var peer in peers)
                        ModerationRow((selectedPlayer.HasValue && peer.Equals(selectedPlayer.Value) ? "> " : "") + peer.Name + " [Player " + peer.ClientId + "]",
                            () => { if (!ModerationAuthority) return; selectedPlayer = peer; confirmBan = null;
                                moderationStatus = "Selected: " + peer.Name; rosterSignature = ""; RefreshModerationRows(); }, true);
                    foreach (ulong id in unresolved) ModerationRow("Connected player - identity unavailable", () => { }, false);
                }
                if (wasInRows) EventSystem.current?.SetSelectedGameObject(moderationBack!.gameObject);
            }
            bool confirming = !showBans && confirmBan.HasValue;
            kickButton!.gameObject.SetActive(!showBans && !confirming);
            banButton!.gameObject.SetActive(!showBans && !confirming);
            confirmButton!.gameObject.SetActive(confirming); cancelBanButton!.gameObject.SetActive(confirming);
            previousBans!.gameObject.SetActive(showBans && pages > 1); nextBans!.gameObject.SetActive(showBans && pages > 1);
            EnableButton(previousBans, banPage > 0); EnableButton(nextBans, banPage + 1 < pages);
            EnableButton(kickButton, selectedPlayer.HasValue); EnableButton(banButton, selectedPlayer.HasValue && bans.Ready);
            moderationInfo!.text = confirming ? "Ban " + confirmBan!.Value.Name + " from your hosted games until unbanned?" :
                bans.Error.Length > 0 ? bans.Error : showBans && moderationStatus == "Select a player to manage."
                    ? "Select an account to unban it locally." : moderationStatus;
            RebuildNavigation(moderationScreen);
        }

        private void ModerationRow(string label, Action action, bool enabledRow)
        {
            var row = CopyButton(browserUi!.RefreshButton, moderationScroll!.content, "ModerationRow", action);
            uiAdditions.Remove(row.gameObject); // Owned by the scroll; avoid retaining destroyed row references.
            row.TextMeshPro.text = label; row.TextMeshPro.fontSize = 24;
            row.TextMeshPro.textWrappingMode = TextWrappingModes.NoWrap;
            row.TextMeshPro.overflowMode = TextOverflowModes.Ellipsis;
            var fit = row.GetComponent<ContentSizeFitter>();
            if (fit != null) fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            var layout = row.GetComponent<LayoutElement>() ?? row.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = 56;
            EnableButton(row, enabledRow);
        }

        private void KickSelected()
        {
            if (!selectedPlayer.HasValue || !ModerationAuthority) return;
            ReportModeration(moderator.Kick(selectedPlayer.Value), false);
        }
        private void BeginBanSelected()
        {
            if (!selectedPlayer.HasValue || !ModerationAuthority) return;
            confirmBan = selectedPlayer;
            RefreshModerationRows();
            EventSystem.current?.SetSelectedGameObject(cancelBanButton!.gameObject);
        }
        private void ConfirmSelectedBan()
        {
            if (!confirmBan.HasValue || !ModerationAuthority) return;
            var target = confirmBan.Value; confirmBan = null;
            ReportModeration(moderator.Ban(target, true), true);
            EventSystem.current?.SetSelectedGameObject(moderationBack!.gameObject);
        }
        private void ReportModeration(ModerationResult result, bool ban)
        {
            moderationStatus = result == ModerationResult.Requested ? (ban ? "Ban saved; disconnect requested." : "Player removed; they may reconnect.") :
                result == ModerationResult.BannedDisconnectFailed ? "Ban saved; disconnect failed. Incoming traffic and reconnects are blocked. Retrying removal." :
                result == ModerationResult.StorageFailed ? bans.Error : "Action refused: " + result + ".";
            rosterSignature = "";
            RefreshModerationRows();
        }

        private void KeepModerationSelectionVisible()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (moderationScroll == null || selected == null || !selected.transform.IsChildOf(moderationScroll.content)) return;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(moderationScroll.viewport, selected.transform);
            var view = moderationScroll.viewport.rect;
            float offset = bounds.max.y > view.yMax ? view.yMax - bounds.max.y : bounds.min.y < view.yMin ? view.yMin - bounds.min.y : 0;
            if (Mathf.Abs(offset) > 0.1f) { moderationScroll.StopMovement(); moderationScroll.content.anchoredPosition += new Vector2(0, offset); }
        }

        private void CloseModerationUi()
        {
            selectedPlayer = confirmBan = null;
            if (moderationScreen == null || GameCanvas.instance == null || GameCanvas.instance.CurrentScreen != moderationScreen) return;
            var target = moderationReturn != null && moderationReturn != moderationScreen ? moderationReturn : pauseUi;
            if (target != null) GameCanvas.instance.SetScreen(target);
        }
        private void RemoveModerationUi()
        {
            if (GameCanvas.instance != null && moderationScreen != null) GameCanvas.instance.Screens.Remove(moderationScreen);
        }
    }
}
