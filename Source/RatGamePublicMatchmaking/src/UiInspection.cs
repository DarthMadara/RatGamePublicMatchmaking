using System;
using System.IO;
using System.Text;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace RatGamePublicMatchmaking
{
    public sealed partial class Plugin
    {
        private bool uiInspected;
        private Gamepad? uiTestGamepad;
        private static string UiDevelopmentPath => Path.Combine(Paths.GameRootPath, "MatchmakingDevelopment", "0.1.0+waiting-browser.1");
        private void InspectNativeUi()
        {
            if (!nativeUiTools.Value || uiInspected || Time.unscaledTime < 8 || GameCanvas.instance == null) return;
            uiInspected = true;
            try
            {
                string directory = Path.Combine(UiDevelopmentPath, "inspection");
                Directory.CreateDirectory(directory);
                var output = new StringBuilder();
                foreach (var screen in GameCanvas.instance.Screens)
                {
                    if (screen == null) continue;
                    output.AppendLine("SCREEN " + screen.GetType().Name);
                    foreach (var field in screen.GetType().GetFields())
                        if (field.GetValue(screen) is Component reference)
                            output.AppendLine("REFERENCE " + field.Name + " -> " + UiPath(reference.transform));
                    DumpUi(screen.transform, output, 0);
                }
                if (PrefabManager.instance != null && PrefabManager.instance.LobbyElementPrefab != null)
                {
                    output.AppendLine("PREFAB LobbyElementPrefab");
                    DumpUi(PrefabManager.instance.LobbyElementPrefab.transform, output, 0);
                }
                File.WriteAllText(Path.Combine(directory, "RuntimeUiHierarchy.txt"), output.ToString());
                Logger.LogInfo("Native UI hierarchy inspected; original asset references recorded locally.");
            }
            catch (Exception error) { Logger.LogWarning("UI inspection: " + error); }
        }

        // Explicitly enabled local development tooling; no network fixtures or synthetic lobbies.
        // Commands exercise the existing Unity pointer/navigation handlers on the Unity main thread.
        private void PollUiCommands()
        {
            if (!nativeUiTools.Value) return;
            string file = Path.Combine(UiDevelopmentPath, "ui-command.txt");
            if (!File.Exists(file)) return;
            string command = File.ReadAllText(file).Trim();
            File.Delete(file);
            try
            {
                if (command.StartsWith("capture ", StringComparison.Ordinal))
                {
                    string label = command.Substring(8);
                    if (label.Length > 60 || label.Length == 0 || Array.Exists(label.ToCharArray(), c => !char.IsLetterOrDigit(c) && c != '-'))
                        throw new ArgumentException("Invalid screenshot label");
                    ScreenCapture.CaptureScreenshot(Path.Combine(UiDevelopmentPath, "screenshots", label + ".png"));
                }
                else if (command == "find") GameCanvas.instance.SetScreen<FindLobbyScreen>();
                else if (command == "title") GameCanvas.instance.SetScreen<StartScreen>();
                else if (command == "settings") GameCanvas.instance.SetScreen<LobbySettingsScreen>();
                else if (command == "pause") GameCanvas.instance.SetScreen<PauseScreen>();
                else if (command == "game") GameCanvas.instance.SetScreen<GameScreen>();
                else if (command == "host") ClickUiButton(titleUi!.HostButton);
                else if (command == "quit") ClickUiButton(titleUi!.QuitButton);
                else if (command == "toggle") ClickUiButton(GameCanvas.instance.CurrentScreen == settingsUi ? settingsHosting! :
                    GameCanvas.instance.CurrentScreen == pauseUi ? pauseHosting! : titleHosting!);
                else if (command == "refresh") ClickUiButton(browserUi!.RefreshButton);
                else if (command == "title-refresh") ClickUiButton(titleRefresh!);
                else if (command == "hover-toggle")
                {
                    var position = RectTransformUtility.WorldToScreenPoint(null, titleHosting!.transform.position);
                    Mouse.current?.WarpCursorPosition(position);
                    ExecuteEvents.Execute(titleHosting.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
                }
                else if (command == "focus-refresh") EventSystem.current.SetSelectedGameObject(titleRefresh!.gameObject);
                else if (command == "preview-title") PreviewTitleRows();
                else if (command == "start-match") ClickUiButton(settingsUi!.PlayButton);
                else if (command == "chef-left") ClickUiButton(settingsUi!.ChangeChefButtonLeft);
                else if (command == "chef-right") ClickUiButton(settingsUi!.ChangeChefButtonRight);
                else if (command == "mode-right") ClickUiButton(settingsUi!.ChangeGameModeButtonRight);
                else if (command == "return-lobby") GameManager.instance.ReturnToLobbyServerRpc();
                else if (command == "probe-public") _ = RefreshPublic(null, browserEpoch);
                else if (command.StartsWith("resolution ", StringComparison.Ordinal))
                {
                    var sizes = command.Substring(11).Split('x');
                    Screen.SetResolution(int.Parse(sizes[0]), int.Parse(sizes[1]), FullScreenMode.Windowed);
                }
                else if (command.StartsWith("ui-scale ", StringComparison.Ordinal))
                {
                    GameSettings.Current.UIScale = (UIScale)int.Parse(command.Substring(9));
                    GameCanvas.instance.UpdateUIScale();
                }
                else if (command == "preview-rows") PreviewLobbyRows();
                else if (command == "left") ClickUiButton(settingsUi!.ChangeVisibilityButtonLeft);
                else if (command == "right") ClickUiButton(settingsUi!.ChangeVisibilityButtonRight);
                else if (command == "back") ClickUiButton(browserUi!.BackButton);
                else if (command == "disconnect") GameManager.instance.DisconnectAndRestart(false);
                else if (command == "f8") showPanel = !showPanel;
                else if (command == "pointer-away") Mouse.current?.WarpCursorPosition(new Vector2(40, 40));
                else if (command == "focus-toggle") EventSystem.current.SetSelectedGameObject(titleHosting!.gameObject);
                else if (command == "move-down") ExecuteEvents.Execute(EventSystem.current.currentSelectedGameObject,
                    new AxisEventData(EventSystem.current) { moveDir = MoveDirection.Down }, ExecuteEvents.moveHandler);
                else if (command == "controller-down")
                {
                    uiTestGamepad ??= InputSystem.AddDevice<Gamepad>();
                    InputSystem.QueueStateEvent(uiTestGamepad, new GamepadState().WithButton(GamepadButton.South));
                }
                else if (command == "controller-up" && uiTestGamepad != null) InputSystem.QueueStateEvent(uiTestGamepad, new GamepadState());
                else if (command == "controller-remove" && uiTestGamepad != null) { InputSystem.RemoveDevice(uiTestGamepad); uiTestGamepad = null; }
                else if (command != "state") throw new ArgumentException("Unknown UI test command");
                File.AppendAllText(Path.Combine(UiDevelopmentPath, "inspection", "UiCommands.log"), DateTime.UtcNow.ToString("O") + " " + command + " OK\n");
                RecordUiState();
            }
            catch (Exception error)
            {
                File.AppendAllText(Path.Combine(UiDevelopmentPath, "inspection", "UiCommands.log"), command + " FAILED: " + error + "\n");
            }
        }

        private void PreviewLobbyRows()
        {
            if (browserUi == null || !browserUi.gameObject.activeInHierarchy || !HasPublicParent(browserUi))
                throw new InvalidOperationException("Open Find Lobby first");
            ClearRows(browserUi.PublicLobbiesParent);
            nativePublicRows.Clear();
            // Display-only fixture, deliberately outside publicResults/publicScopes.
            // No Lobby objects, Steam calls, join callbacks or admission changes.
            publicBrowserState = "LAYOUT PREVIEW - no Steam lobbies. Join disabled.";
            int number = 0;
            foreach (string name in new[] { "Kitchen crew", "The Ratatouille Department", "A very long restaurant host name to check that the Join button stays visible" })
            {
                var row = Instantiate(PrefabManager.instance.LobbyElementPrefab, browserUi.PublicLobbiesParent, false);
                row.name = "RGPM_PreviewRow";
                var count = StylePublicRow(row);
                count.text = (++number == 1 ? "2" : number == 2 ? "9" : "10") + "/10";
                row.LobbyNameText.text = name;
                row.JoinButton.ResetEvents();
                row.JoinButton.TextMeshPro.text = number == 3 ? "Full" : "Join";
                foreach (var loc in row.JoinButton.GetComponentsInChildren<MonoBehaviour>(true))
                    if (loc.GetType().Name == "LocSetter") loc.enabled = false;
                EnableButton(row.JoinButton, false);
            }
            RebuildNavigation(browserUi);
        }

        private void PreviewTitleRows()
        {
            if (titleScroll == null) throw new InvalidOperationException("Title browser not installed");
            ClearRows(titleScroll.content);
            titlePublicRows.Clear();
            nextTitleRefresh = Time.unscaledTime + 120;
            publicBrowserState = "LAYOUT PREVIEW - no Steam lobbies. Join disabled.";
            titleBrowserStatus!.text = publicBrowserState;
            int i = 0;
            foreach (string name in new[] { "Kitchen crew", "The Ratatouille Department", "A very long restaurant lobby name to check Join remains visible" })
            {
                var row = Instantiate(PrefabManager.instance.LobbyElementPrefab, titleScroll.content, false);
                row.name = "RGPM_LayoutPreview";
                var count = StylePublicRow(row);
                count.text = (++i == 1 ? "2" : i == 2 ? "9" : "10") + "/10";
                row.LobbyNameText.text = name;
                row.JoinButton.ResetEvents();
                foreach (var loc in row.JoinButton.GetComponentsInChildren<MonoBehaviour>(true))
                    if (loc.GetType().Name == "LocSetter") loc.enabled = false;
                row.JoinButton.TextMeshPro.text = i == 3 ? "Full" : "Join";
                EnableButton(row.JoinButton, false);
            }
            RebuildNavigation(titleUi!);
        }

        private static void ClickUiButton(CustomButton button)
        {
            if (!button.isActiveAndEnabled || !button.ButtonEnabled || !button.IsInteractable()) throw new InvalidOperationException("Button is disabled");
            var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerExitHandler);
        }

        private void RecordUiState()
        {
            LogLobbySnapshot("ui-state");
            var output = new StringBuilder();
            output.AppendLine("Screen=" + GameCanvas.instance.CurrentScreen?.GetType().Name + "; Steam=" + Steamworks.SteamClient.IsValid +
                "; Host=" + HostReady + "; Requested=" + publicHosting.Value + "; Advertised=" + IsPublicHost + "; Status=" + status);
            output.AppendLine("Selected=" + EventSystem.current.currentSelectedGameObject?.name);
            output.AppendLine("Lifecycle=" + hostPhase + "; lobbyLoadConfirmed=" + lobbyLoadConfirmed + "; gameState=" +
                GameManager.instance?.CurrentGameState.Value + "; uiScale=" + GameSettings.Current.UIScale);
            if (titleBrowser != null)
            {
                var corners = new Vector3[4]; titleBrowser.GetWorldCorners(corners);
                output.AppendLine("TitleBrowser bounds=" + corners[0] + " -> " + corners[2] + "; safe=" + Screen.safeArea +
                    "; nativeRows=" + titlePublicRows.Count + "; renderedRows=" + titleScroll!.content.childCount);
            }
            foreach (var tooltip in GameCanvas.instance.GetComponentsInChildren<Tooltip>(true))
                output.AppendLine("Tooltip active=" + tooltip.TooltipTransform.gameObject.activeSelf + " labelWorldRotation=" + tooltip.TextMesh.transform.eulerAngles +
                    " paperWorldRotation=" + tooltip.TooltipTransform.eulerAngles + " text=" + tooltip.TextMesh.text);
            foreach (var screen in GameCanvas.instance.Screens)
            {
                output.AppendLine(screen.GetType().Name + " componentEnabled=" + screen.enabled + " active=" + screen.gameObject.activeInHierarchy +
                    " RGPM objects=" + Array.FindAll(screen.GetComponentsInChildren<Transform>(true), t => t.name.StartsWith("RGPM_", StringComparison.Ordinal)).Length);
                if (!screen.gameObject.activeInHierarchy) continue;
                foreach (var button in screen.GetComponentsInChildren<CustomButton>())
                    output.AppendLine("Button " + button.name + " enabled=" + button.ButtonEnabled + " label=" + button.TextMeshPro?.text +
                        " down=" + button.navigation.selectOnDown?.name + " up=" + button.navigation.selectOnUp?.name);
            }
            File.AppendAllText(Path.Combine(UiDevelopmentPath, "inspection", "UiState.log"), output.ToString() + "\n");
        }

        private static string UiPath(Transform node) => node.parent == null ? node.name : UiPath(node.parent) + "/" + node.name;
        private static void DumpUi(Transform node, StringBuilder output, int depth)
        {
            string indent = new string(' ', depth * 2);
            output.Append(indent).Append(node.name).Append(" active=").Append(node.gameObject.activeSelf);
            if (node is RectTransform rect)
                output.Append(" rect=").Append(rect.rect).Append(" anchor=").Append(rect.anchorMin).Append("/").Append(rect.anchorMax)
                    .Append(" pos=").Append(rect.anchoredPosition).Append(" size=").Append(rect.sizeDelta).Append(" pivot=").Append(rect.pivot);
            output.AppendLine();
            foreach (var component in node.GetComponents<Component>())
            {
                if (component == null || component is Transform) continue;
                output.Append(indent).Append("  ").Append(component.GetType().FullName);
                if (component is TMP_Text text)
                    output.Append(" text=").Append(text.text.Replace("\n", "\\n")).Append(" font=").Append(text.font?.name)
                        .Append(" material=").Append(text.fontSharedMaterial?.name).Append(" color=").Append(text.color)
                        .Append(" fontSize=").Append(text.fontSize).Append(" alignment=").Append(text.alignment);
                if (component is Image image)
                    output.Append(" sprite=").Append(image.sprite?.name).Append(" material=").Append(image.material?.name)
                        .Append(" color=").Append(image.color).Append(" type=").Append(image.type);
                if (component is CustomButton button)
                    output.Append(" textRef=").Append(button.TextMeshPro == null ? "null" : UiPath(button.TextMeshPro.transform))
                        .Append(" imageRef=").Append(button.Image == null ? "null" : UiPath(button.Image.transform))
                        .Append(" controller=").Append(button.SelectableWithController).Append(" navigation=").Append(button.navigation.mode);
                if (component is LayoutGroup layout)
                    output.Append(" padding=").Append(layout.padding);
                if (component is VerticalLayoutGroup vertical)
                    output.Append(" spacing=").Append(vertical.spacing).Append(" control=").Append(vertical.childControlWidth).Append("/").Append(vertical.childControlHeight);
                output.AppendLine();
            }
            for (int i = 0; i < node.childCount; i++) DumpUi(node.GetChild(i), output, depth + 1);
        }
    }
}


