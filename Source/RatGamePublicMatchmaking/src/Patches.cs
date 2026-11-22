using System;
using System.Threading.Tasks;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;

namespace RatGamePublicMatchmaking
{
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartGameServerRpc))]
    internal static class MatchStartPatch
    {
        private static void Prefix(GameManager __instance) => Plugin.Instance?.BeforeMatchStart(__instance);
    }

    [HarmonyPatch(typeof(GameManager), "ReturnToLobby")]
    internal static class LobbyReturnPatch
    {
        private static void Prefix(GameManager __instance) => Plugin.Instance?.BeforeLobbyReturn(__instance);
    }

    [HarmonyPatch(typeof(GameManager), "SceneManager_HostLoadCompleted")]
    internal static class HostLoadPatch
    {
        private static void Postfix(GameManager __instance, string __0, System.Collections.Generic.List<ulong> __3) =>
            Plugin.Instance?.HostSceneCompleted(__instance, __0, __3);
    }

    [HarmonyPatch(typeof(Tooltip), "LateUpdate")]
    internal static class PublicTooltipPatch
    {
        private static void Postfix(Tooltip __instance) => Plugin.Instance?.LevelPublicTooltip(__instance);
    }

    [HarmonyPatch(typeof(SteamNetworkManager), "OnLobbyCreated")]
    internal static class CreatedPatch
    {
        private static void Prefix(Result result, Lobby lobby) => Plugin.Instance?.BeginLobbyDiagnostics(result, lobby);
        private static void Postfix(Result result, Lobby lobby) => Plugin.Instance?.LobbyCreated(result, lobby);
        private static Exception? Finalizer(Exception? __exception)
        {
            Plugin.Instance?.NativeCreationFailure(__exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ChangeVisibilityServerRpc))]
    internal static class VisibilityPatch
    {
        private static bool Prefix(GameManager __instance, out bool __state)
        {
            __state = false;
            var plugin = Plugin.Instance;
            // NGO's generated method runs for both sending and server execution.
            if (plugin == null || !__instance.IsServer || Convert.ToInt32(Plugin.RpcStage.GetValue(__instance)) != 1)
                return true;
            __state = true;
            return plugin.BeforeNativeVisibility();
        }
        private static void Postfix(bool __state)
        {
            if (__state) Plugin.Instance?.AfterNativeVisibility();
        }
    }

    [HarmonyPatch(typeof(SteamNetworkManager), nameof(SteamNetworkManager.Disconnect))]
    internal static class DisconnectPatch
    {
        private static void Prefix() => Plugin.Instance?.Disconnecting();
        private static void Postfix() => Plugin.Instance?.AfterDisconnect();
    }

    [HarmonyPatch(typeof(StartScreen), "GetBuildId")]
    internal static class NativeBuildIdDiagnosticPatch
    {
        private static Exception? Finalizer(Exception? __exception)
        {
            Plugin.Instance?.NativeBuildIdFailure(__exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Lobby), nameof(Lobby.SetPublic))]
    internal static class PublicSetterDiagnosticPatch
    {
        private static void Postfix(Lobby __instance, bool __result) => Plugin.Instance?.RecordTypeSetter(__instance, "Public", __result);
    }
    [HarmonyPatch(typeof(Lobby), nameof(Lobby.SetInvisible))]
    internal static class InvisibleSetterDiagnosticPatch
    {
        private static void Postfix(Lobby __instance, bool __result) => Plugin.Instance?.RecordTypeSetter(__instance, "Invisible", __result);
    }
    [HarmonyPatch(typeof(Lobby), nameof(Lobby.SetFriendsOnly))]
    internal static class FriendsSetterDiagnosticPatch
    {
        private static void Postfix(Lobby __instance, bool __result) => Plugin.Instance?.RecordTypeSetter(__instance, "FriendsOnly", __result);
    }
    [HarmonyPatch(typeof(Lobby), nameof(Lobby.SetPrivate))]
    internal static class PrivateSetterDiagnosticPatch
    {
        private static void Postfix(Lobby __instance, bool __result) => Plugin.Instance?.RecordTypeSetter(__instance, "Private", __result);
    }
    [HarmonyPatch(typeof(Lobby), nameof(Lobby.SetJoinable))]
    internal static class JoinableSetterDiagnosticPatch
    {
        private static void Postfix(Lobby __instance, bool __0, bool __result) => Plugin.Instance?.RecordJoinableSetter(__instance, __0, __result);
    }
    [HarmonyPatch(typeof(Lobby), nameof(Lobby.SetData))]
    internal static class MetadataSetterDiagnosticPatch
    {
        private static void Postfix(Lobby __instance, string __0, string __1, bool __result) =>
            Plugin.Instance?.RecordMetadataSetter(__instance, __0, __1, __result);
    }
    [HarmonyPatch(typeof(Lobby), nameof(Lobby.Leave))]
    internal static class LeaveDiagnosticPatch
    {
        private static void Postfix(Lobby __instance) => Plugin.Instance?.RecordLeave(__instance);
    }

    [HarmonyPatch(typeof(FindLobbyScreen), "UpdateLobbies")]
    internal static class BrowserPatch
    {
        private static bool Prefix(FindLobbyScreen __instance, ref Task __result)
        {
            if (Plugin.Instance == null) return true;
            __result = Plugin.Instance.RefreshScreen(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(FindLobbyScreen), "OnEnable")]
    internal static class BrowserEnablePatch
    {
        private static void Postfix() => Plugin.Instance?.InvalidateBrowser();
    }

    [HarmonyPatch(typeof(SteamNetworkManager), nameof(SteamNetworkManager.JoinLobby))]
    internal static class JoinPatch
    {
        private static bool Prefix(SteamNetworkManager __instance, SteamId lobbyId) =>
            Plugin.Instance?.NativeJoinRequested(__instance, lobbyId) ?? true;
    }

    [HarmonyPatch(typeof(SteamNetworkManager), "OnLobbyEntered")]
    internal static class EnterPatch
    {
        private static bool Prefix(Lobby lobby) => Plugin.Instance?.AllowLobbyEntry(lobby) ?? true;
    }

    [HarmonyPatch(typeof(SteamNetworkManager), "OnGameLobbyJoinRequested")]
    internal static class InvitePatch
    {
        private static bool Prefix(Lobby lobby) => Plugin.Instance?.AllowNativeInvite(lobby) ?? true;
    }

    [HarmonyPatch(typeof(TransitionScreen), nameof(TransitionScreen.StartTransitionAsync))]
    internal static class DeferredConnectionPatch
    {
        private static void Prefix(ref Action __0) => Plugin.Instance?.GuardDeferredConnection(ref __0);
    }

    [HarmonyPatch(typeof(LobbySettingsScreen), "Update")]
    internal static class SettingsLabelPatch
    {
        private static void Postfix(LobbySettingsScreen __instance)
        {
            if (Plugin.Instance?.IsPublicHost == true && __instance.LobbyVisibilityText != null)
                __instance.LobbyVisibilityText.text = "Public - advertised";
        }
    }

    [HarmonyPatch(typeof(ChangeLobbyVisibilityInteractable), "Update")]
    internal static class WorldLabelPatch
    {
        private static void Postfix(ChangeLobbyVisibilityInteractable __instance)
        {
            if (Plugin.Instance?.IsPublicHost == true && __instance.VisibilityText != null)
                __instance.VisibilityText.text = "Public - advertised";
        }
    }
}
