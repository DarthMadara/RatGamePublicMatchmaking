using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ModerationVerification
{
    internal static void Run(ModuleDefinition plugin, ModuleDefinition game, string managed, ReaderParameters parameters)
    {
        var types = All(plugin.Types).ToArray();
        var methods = types.SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        TypeDefinition Type(string name) => types.Single(t => t.Name == name);
        MethodDefinition Method(string type, string name) => Type(type).Methods.Single(m => m.Name == name);
        bool Calls(MethodDefinition method, string type, string name) => method.Body.Instructions.Any(i =>
            i.Operand is MethodReference called && called.DeclaringType.Name == type && called.Name == name);
        void Require(bool value, string detail) { if (!value) throw new Exception("Moderation contract: " + detail); }
        foreach (var (patch, handler) in new[] {
            ("ModerationConnectingPatch", "AllowSteamConnecting"), ("ModerationConnectedPatch", "AllowSteamConnected"),
            ("ModerationMessagePatch", "AllowSteamMessage"), ("ModerationDisconnectedPatch", "SteamDisconnected"),
            ("ModerationRemoteClosedPatch", "SteamRemoteClosed"), ("ModerationTransportStoppedPatch", "SteamTransportStopped") })
        {
            var callback = Type(patch).Methods.Single(m => m.Name is "Prefix" or "Postfix");
            Require(Calls(callback, "Plugin", handler), patch + " must call its real host handler");
        }
        Require(Calls(Method("ModerationApprovalPatch", "Prefix"), "Plugin", "GuardNgoApproval") &&
            Calls(Method("Plugin", "GuardNgoApproval"), "SteamConnections", "AllowsAdmission") &&
            Calls(Method("Plugin", "GuardNgoApproval"), "NetworkManager", "GetTransportIdFromClientId"), "final approval rechecks queued authenticated connections");
        foreach (string stage in new[] { "AllowSteamConnecting", "AllowSteamConnected", "AllowSteamMessage" })
        {
            var handler = Method("Plugin", stage);
            Require(Calls(handler, "Plugin", "IsModeratedTransport") && Calls(handler, "Plugin", "CloseRejected"), stage + " host scope / rejection");
        }
        Require(Calls(Method("Plugin", "AllowSteamConnecting"), "BanStore", "Allows"), "early Steam ban rejection");
        Require(Calls(Method("Plugin", "AllowSteamConnected"), "Plugin", "ReadRemoteSteam") &&
            Calls(Method("Plugin", "AllowSteamConnected"), "BanStore", "Allows") &&
            Calls(Method("Plugin", "AllowSteamConnected"), "SteamConnections", "Bind"), "identity captured before native NGO connect");
        Require(Calls(Method("Plugin", "AllowSteamMessage"), "SteamConnections", "AllowsMessage"), "pending approval and message admission");
        Require(Calls(Method("Plugin", "ReadRemoteSteam"), "NetIdentity", "get_IsSteamId") &&
            Calls(Method("Plugin", "ReadRemoteSteam"), "NetIdentity", "get_SteamId") &&
            Calls(Method("Plugin", "ReadRemoteSteam"), "FieldInfo", "GetValue"), "SDK raw identity accessor");
        Require(!methods.Any(m => Calls(m, "ConnectionInfo", "get_Identity")), "never trust defective wrapper Identity getter");
        Require(!methods.Any(m => m.CustomAttributes.Any(a => a.AttributeType.Name is "ServerRpcAttribute" or "ClientRpcAttribute" or "RpcAttribute")),
            "mod must expose no RPC moderation endpoint");
        Require(!methods.Any(m => m.Body.Instructions.Any(i => i.Operand is MethodReference called &&
            (called.Name.Contains("RegisterNamedMessageHandler") || called.Name.Contains("RegisterUnnamedMessageHandler")))), "no remote command registration");
        var resolve = Method("SteamModerationSession", "Resolve");
        Require(Calls(resolve, "NetworkManager", "GetTransportIdFromClientId") && Calls(resolve, "NetworkManager", "GetClientIdFromTransportId") &&
            Calls(resolve, "NetworkManager", "get_ConnectedClients") && Calls(resolve, "SteamConnections", "TryGet"), "separate current NGO/transport/Steam mapping");
        Require(Calls(Method("SteamModerationSession", "Disconnect"), "NetworkManager", "DisconnectClient") &&
            !Calls(Method("SteamModerationSession", "Disconnect"), "NetworkManager", "Shutdown"), "single remote native disconnect");
        var ban = Method("HostModerator", "Ban");
        int addIndex = ban.Body.Instructions.ToList().FindIndex(i => i.Operand is MethodReference m && m.Name == "Add" && m.DeclaringType.Name == "BanStore");
        int disconnectIndex = ban.Body.Instructions.ToList().FindIndex(i => i.Operand is MethodReference m && m.Name == "Disconnect");
        Require(addIndex >= 0 && disconnectIndex > addIndex, "durable ban before disconnect");
        Require(Calls(Method("AtomicBanFile", "Commit"), "FileStream", "Flush") && Calls(Method("AtomicBanFile", "Commit"), "File", "Replace"), "flush and atomic replacement");
        Require(Calls(Method("Plugin", "Update"), "Plugin", "PollModeration") && Calls(Method("Plugin", "Disconnecting"), "Plugin", "ResetModerationConnections"),
            "poll and host disconnect cleanup");
        Require(Calls(Method("Plugin", "OpenModeration"), "GameCanvas", "SetScreen") && Type("ModerationScreen").BaseType.FullName == "SokScreen",
            "native screen and input routing");

        using var steam = ModuleDefinition.ReadModule(Path.Combine(managed, "Facepunch.Steamworks.Win64.dll"), parameters);
        using var transport = ModuleDefinition.ReadModule(Path.Combine(managed, "Facepunch Transport for Netcode for GameObjects.dll"), parameters);
        using var ngo = ModuleDefinition.ReadModule(Path.Combine(managed, "Unity.Netcode.Runtime.dll"), parameters);
        var info = steam.Types.Single(t => t.FullName == "Steamworks.Data.ConnectionInfo");
        Require(info.IsSequentialLayout && info.Fields.First().Name == "identity" && info.Fields.First().FieldType.FullName == "Steamworks.Data.NetIdentity",
            "SDK-marshalled identity layout");
        var brokenGetter = info.Methods.Single(m => m.Name == "get_Identity");
        Require(brokenGetter.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "address"), "known wrapper defect must be re-investigated if changed");
        var client = transport.Types.Single(t => t.Name == "FacepunchTransport");
        var connected = client.Methods.Single(m => m.Name == "Steamworks.ISocketManager.OnConnected");
        Require(Calls(connected, "Connection", "get_Id") && Calls(connected, "NetworkTransport", "InvokeOnTransportEvent"), "native transport handle to NGO");
        var cm = ngo.Types.Single(t => t.Name == "NetworkConnectionManager");
        var connect = cm.Methods.Single(m => m.Name == "ConnectEventHandler");
        foreach (string field in new[] { "ClientIdToTransportIdMap", "TransportIdToClientIdMap", "m_NextClientId" })
            Require(connect.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == field), "native separate client assignment: " + field);
        var socket = steam.Types.Single(t => t.Name == "SteamNetworkingSockets").Methods.Single(m => m.Name == "CreateRelaySocket" && m.HasGenericParameters);
        Require(Calls(socket, "ISteamNetworkingSockets", "CreateListenSocketP2P"), "authenticated Steam P2P path");
        var received = steam.Types.Single(t => t.Name == "SocketManager").Methods.Single(m => m.Name == "ReceiveMessage");
        Require(received.Body.Instructions.Any(i => i.Operand is FieldReference f && f.DeclaringType.Name == "NetMsg" && f.Name == "Identity"),
            "message identity must originate in the SDK-marshalled envelope");
        Require(game.Types.Single(t => t.Name == "GameCanvas").Methods.Any(m => m.Name == "SetScreen" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "SokScreen"),
            "native screen entry point");
        Console.WriteLine("Moderation identity layout, mapping, transport admission, persistence ordering, cleanup, and native UI wiring statically verified.");
    }
    private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots) { yield return type; foreach (var nested in All(type.NestedTypes)) yield return nested; }
    }
}
