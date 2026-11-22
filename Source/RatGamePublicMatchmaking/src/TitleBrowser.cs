using System;
using System.Collections.Generic;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private RectTransform? titleBrowser;
        private ScrollRect? titleScroll;
        private TMP_Text? titleBrowserStatus;
        private TMP_Text? titleBrowserHeading;
        private CustomButton? titleRefresh, titleCancel;
        private readonly List<PublicRow> titlePublicRows = new List<PublicRow>();
        private bool titleWasActive;
        private float nextTitleRefresh;
        private Vector2 lastBrowserSize;
        private Tooltip? levelledTooltip;
        private Quaternion tooltipOriginalRotation;
        private Quaternion tooltipOriginalBoxRotation;
        private bool tooltipInspected;

        private void LateUpdate()
        {
            if (GameCanvas.instance == null || GameCanvas.instance.MyCanvas == null) return;
            foreach (var button in new[] { titleHosting, settingsHosting, pauseHosting })
                if (button != null && button.TextMeshPro != null && button.TextMeshPro.transform != button.transform)
                    button.TextMeshPro.transform.rotation = GameCanvas.instance.MyCanvas.transform.rotation;
        }

        private void InstallTitleBrowser(StartScreen title, FindLobbyScreen browser)
        {
            var source = browser.PublicLobbiesParent.GetComponentInParent<ScrollRect>(true);
            var paper = browser.transform.Find("Box")?.GetComponent<Image>();
            if (source == null || paper == null || browser.RefreshButton == null || source.content == null) return;
            titleBrowser = new GameObject("RGPM_TitlePublicLobbies", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<RectTransform>();
            uiAdditions.Add(titleBrowser.gameObject);
            titleBrowser.SetParent(title.transform, false);
            var image = titleBrowser.GetComponent<Image>();
            image.sprite = paper.sprite;
            image.type = paper.type;
            image.color = paper.color;
            image.material = paper.material;
            var shadow = titleBrowser.gameObject.AddComponent<Shadow>();
            var originalShadow = paper.GetComponent<Shadow>();
            if (originalShadow != null) { shadow.effectColor = originalShadow.effectColor; shadow.effectDistance = originalShadow.effectDistance; }

            var heading = CopyText(title.HostButton.TextMeshPro, titleBrowser, "TitlePublicHeading", 32);
            titleBrowserHeading = heading;
            heading.text = "Public Lobbies";
            heading.color = title.HostButton.TextMeshPro.color;
            heading.alignment = TextAlignmentOptions.MidlineLeft;
            Stretch(heading.rectTransform, 24, 24, 22, 0, 44);
            titleBrowserStatus = CopyText(title.HostButton.TextMeshPro, titleBrowser, "TitlePublicStatus", 22);
            titleBrowserStatus.alignment = TextAlignmentOptions.TopLeft;
            titleBrowserStatus.textWrappingMode = TextWrappingModes.Normal;
            Stretch(titleBrowserStatus.rectTransform, 24, 24, 76, 0, 70);

            titleScroll = Instantiate(source, titleBrowser, false);
            titleScroll.name = "RGPM_TitlePublicScroll";
            Stretch((RectTransform)titleScroll.transform, 16, 16, 150, 94);
            foreach (Transform child in titleScroll.viewport)
                if (child != titleScroll.content) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            ClearRows(titleScroll.content);
            titleScroll.horizontal = false;
            titleScroll.vertical = true;
            titleScroll.movementType = ScrollRect.MovementType.Clamped;
            var content = titleScroll.content;
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1); content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>() ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 8, 8);
            layout.spacing = 12; layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            var fitter = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained; fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            titleRefresh = CopyButton(browser.RefreshButton, titleBrowser, "TitleRefresh", () => _ = RefreshPublic(null, browserEpoch));
            titleRefresh.TextMeshPro.text = "Refresh";
            PlaceFooter(titleRefresh, true);
            titleCancel = CopyButton(browser.RefreshButton, titleBrowser, "TitleCancel", CancelPublicJoin);
            titleCancel.TextMeshPro.text = "Cancel join";
            PlaceFooter(titleCancel, false);
            titleCancel.gameObject.SetActive(false);
            LayoutTitleBrowser();
            RenderTitleRows();
            Logger.LogInfo("Main menu public browser installed using native paper, font, scroll view and LobbyElement prefab.");
        }

        private static void Stretch(RectTransform rect, float left, float right, float top, float bottom, float height = 0)
        {
            rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
            rect.anchorMin = new Vector2(0, height > 0 ? 1 : 0); rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 1);
            rect.offsetMin = new Vector2(left, height > 0 ? -top - height : bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static void PlaceFooter(CustomButton button, bool right)
        {
            var rect = (RectTransform)button.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(right ? 1 : 0, 0);
            rect.pivot = new Vector2(right ? 1 : 0, 0);
            rect.anchoredPosition = new Vector2(right ? -26 : 26, 22);
            rect.sizeDelta = new Vector2(180, 56);
            var fit = button.GetComponent<ContentSizeFitter>();
            if (fit != null) { fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.Unconstrained; }
            var layout = button.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = true;
        }

        private void LayoutTitleBrowser()
        {
            if (titleBrowser == null || titleUi == null) return;
            var parent = (RectTransform)titleUi.transform;
            var canvas = GameCanvas.instance.MyCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var safe = Screen.safeArea;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, safe.min, camera, out var min);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, safe.max, camera, out var max);
            var corners = new Vector3[4];
            ((RectTransform)titleUi.HostButton.transform.parent).GetWorldCorners(corners);
            float menuRight = parent.InverseTransformPoint(corners[2]).x;
            float available = Mathf.Max(100, max.x - Mathf.Max(menuRight + 30, min.x) - 24);
            float scale = Mathf.Min(1, available / 670, (max.y - min.y - 140) / 720);
            scale = Mathf.Max(0.1f, scale);
            titleBrowser.anchorMin = titleBrowser.anchorMax = new Vector2(0.5f, 0.5f);
            titleBrowser.pivot = new Vector2(1, 0.5f);
            titleBrowser.sizeDelta = new Vector2(670, 720);
            titleBrowser.localScale = Vector3.one * scale;
            titleBrowser.anchoredPosition = new Vector2(max.x - 24, (min.y + max.y) / 2);
            var size = new Vector2(parent.rect.width, parent.rect.height) * scale;
            if (lastBrowserSize != size) { lastBrowserSize = size; RebuildNavigation(titleUi); }
        }

        private void PollTitleBrowser()
        {
            if (titleBrowser == null && titleUi != null && browserUi != null) InstallTitleBrowser(titleUi, browserUi);
            if (titleBrowser == null || titleUi == null) return;
            bool active = titleUi.gameObject.activeInHierarchy && GameCanvas.instance.CurrentScreen == titleUi;
            if (active)
            {
                LayoutTitleBrowser();
                titleBrowserHeading!.text = "Public Lobbies";
                bool joining = pendingJoin != null && !pendingJoin.ConnectionConfirmed;
                titleBrowserStatus!.text = !SteamClient.IsValid ? "Steam is unavailable. Connect to Steam to browse public lobbies." : joining ? status : publicBrowserState;
                bool refreshEnabled = SteamClient.IsValid && CanJoin && !joining && !publicRefreshInProgress && !publicQueries.IsBusy && Time.unscaledTime - lastQueryAt >= 2;
                bool navChanged = titleRefresh!.ButtonEnabled != refreshEnabled || titleCancel!.gameObject.activeSelf != joining;
                EnableButton(titleRefresh, refreshEnabled);
                titleCancel!.gameObject.SetActive(joining);
                if (navChanged || !titleWasActive) RebuildNavigation(titleUi);
                UpdatePublicRowStates();
                KeepTitleSelectionVisible();
                if ((!titleWasActive || Time.unscaledTime >= nextTitleRefresh) && !publicRefreshInProgress && !publicQueries.IsBusy && CanJoin && !joining)
                {
                    nextTitleRefresh = Time.unscaledTime + 30;
                    _ = RefreshPublic(null, browserEpoch);
                }
            }
            titleWasActive = active;
        }

        private void RenderTitleRows()
        {
            if (titleScroll == null || titleUi == null) return;
            bool selectionRemoved = EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null &&
                EventSystem.current.currentSelectedGameObject.transform.IsChildOf(titleScroll.content);
            ClearRows(titleScroll.content);
            titlePublicRows.Clear();
            foreach (var lobby in publicResults)
            {
                if (!publicScopes.TryGetValue(lobby.Id, out var scope)) continue;
                var row = Instantiate(PrefabManager.instance.LobbyElementPrefab, titleScroll.content, false);
                row.name = "RGPM_TitleLobbyRow";
                var rowSize = row.GetComponent<LayoutElement>() ?? row.gameObject.AddComponent<LayoutElement>();
                rowSize.minHeight = rowSize.preferredHeight = 68;
                var count = StylePublicRow(row);
                row.JoinButton.ResetEvents();
                row.JoinButton.Clicked += () => BeginPublicJoin(lobby, scope);
                foreach (var loc in row.JoinButton.GetComponentsInChildren<MonoBehaviour>(true))
                    if (loc.GetType().Name == "LocSetter") loc.enabled = false;
                titlePublicRows.Add(new PublicRow { Element = row, PlayerCount = count, Lobby = lobby });
            }
            UpdatePublicRowStates();
            RebuildNavigation(titleUi);
            if (selectionRemoved && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(titleRefresh!.gameObject);
        }

        private void KeepTitleSelectionVisible()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (titleScroll == null || selected == null || !selected.transform.IsChildOf(titleScroll.content)) return;
            var row = selected.GetComponentInParent<LobbyElement>();
            if (row == null) return;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(titleScroll.viewport, (RectTransform)row.transform);
            var view = titleScroll.viewport.rect;
            float offset = bounds.max.y > view.yMax ? view.yMax - bounds.max.y : bounds.min.y < view.yMin ? view.yMin - bounds.min.y : 0;
            if (Mathf.Abs(offset) > 0.1f) { titleScroll.StopMovement(); titleScroll.content.anchoredPosition += new Vector2(0, offset); }
        }

        internal void LevelPublicTooltip(Tooltip tooltip)
        {
            bool ours = tooltip.TooltipTransform.gameObject.activeSelf &&
                Array.Exists(new[] { titleHosting, settingsHosting, pauseHosting }, b => b != null && b.isActiveAndEnabled &&
                    tooltip.TextMesh.text == b.TooltipText);
            if (!ours) { RestoreTooltipRotation(); return; }
            if (levelledTooltip != tooltip)
            {
                RestoreTooltipRotation();
                levelledTooltip = tooltip;
                tooltipOriginalRotation = tooltip.TextMesh.transform.localRotation;
                tooltipOriginalBoxRotation = tooltip.TooltipTransform.localRotation;
                if (nativeUiTools.Value && !tooltipInspected)
                {
                    tooltipInspected = true;
                    var output = new System.Text.StringBuilder();
                    for (Transform node = tooltip.TextMesh.transform; node != null; node = node.parent)
                        output.AppendLine(UiPath(node) + " local=" + node.localEulerAngles + " world=" + node.eulerAngles + " scale=" + node.localScale);
                    DumpUi(tooltip.TooltipTransform, output, 0);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(UiDevelopmentPath, "inspection", "TooltipBeforeCorrection.txt"), output.ToString());
                }
            }
            // Level this public tooltip's paper and label. Keep native position, scale and sounds.
            tooltip.TooltipTransform.rotation = GameCanvas.instance.MyCanvas.transform.rotation;
            tooltip.TextMesh.transform.rotation = GameCanvas.instance.MyCanvas.transform.rotation;
        }

        private void RestoreTooltipRotation()
        {
            if (levelledTooltip != null)
            {
                levelledTooltip.TooltipTransform.localRotation = tooltipOriginalBoxRotation;
                levelledTooltip.TextMesh.transform.localRotation = tooltipOriginalRotation;
            }
            levelledTooltip = null;
        }
    }
}

