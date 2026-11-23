using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

try
{
if (args.Length == 3 && args[0] == "--verify-runtime")
{
    RuntimeVerification.Run(args[1], args[2]);
    return;
}
if (args.Length >= 3 && args[0] == "--inspect-type")
{
    using var input = ModuleDefinition.ReadModule(args[1]);
    var type = AllTypes(input.Types).Single(t => t.FullName == args[2]);
    Console.WriteLine(type.FullName + " BASE " + type.BaseType?.FullName);
    foreach (var nested in type.NestedTypes) Console.WriteLine("NESTED " + nested.FullName);
    foreach (var property in type.Properties) Console.WriteLine("PROPERTY " + property.FullName);
    foreach (var field in type.Fields) Console.WriteLine("FIELD " + field.FullName);
    foreach (var method in type.Methods.Where(m => m.Name == (args.Length > 3 ? args[3] : "Awake") || (args.Length > 3 && args[3] == "*")))
    {
        Console.WriteLine("METHOD " + method.FullName);
        if (method.HasBody) foreach (var instruction in method.Body.Instructions) Console.WriteLine(instruction);
    }
    return;
}
if (args.Length == 3 && args[0] == "--inspect-api")
{
    using var input = ModuleDefinition.ReadModule(args[1]);
    var type = AllTypes(input.Types).Single(t => t.FullName == args[2]);
    Console.WriteLine(type.FullName + " " + type.Attributes);
    foreach (var field in type.Fields) Console.WriteLine(field.FullName + " " + field.Attributes);
    foreach (var method in type.Methods) Console.WriteLine(method.FullName + " " + method.Attributes);
    return;
}
if (args.Length < 3) throw new ArgumentException("Arguments: project root, game Managed directory, BepInEx core directory");
string root = Path.GetFullPath(args[0]);
string managed = Path.GetFullPath(args[1]);
string core = Path.GetFullPath(args[2]);
string pluginPath = Path.Combine(root, "src", "bin", "Release", "netstandard2.1", "RatGamePublicMatchmaking.dll");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(core);
resolver.AddSearchDirectory(Path.GetDirectoryName(pluginPath)!);
var parameters = new ReaderParameters { AssemblyResolver = resolver };
using var plugin = ModuleDefinition.ReadModule(pluginPath, parameters);
using var game = ModuleDefinition.ReadModule(Path.Combine(managed, "Assembly-CSharp.dll"), parameters);
int referencesChecked = 0, patchCount = 0;
var patches = new List<string>();
var allowedTargets = new HashSet<string>
{
    "GameManager.StartGameServerRpc", "GameManager.ReturnToLobby", "GameManager.SceneManager_HostLoadCompleted", "Tooltip.LateUpdate",
    "SteamNetworkManager.OnLobbyCreated", "GameManager.ChangeVisibilityServerRpc", "SteamNetworkManager.Disconnect",
    "FindLobbyScreen.UpdateLobbies", "FindLobbyScreen.OnEnable", "SteamNetworkManager.JoinLobby",
    "SteamNetworkManager.OnLobbyEntered", "SteamNetworkManager.OnGameLobbyJoinRequested",
    "TransitionScreen.StartTransitionAsync", "LobbySettingsScreen.Update", "ChangeLobbyVisibilityInteractable.Update"
    , "StartScreen.GetBuildId", "Steamworks.Data.Lobby.SetPublic", "Steamworks.Data.Lobby.SetInvisible",
    "Steamworks.Data.Lobby.SetFriendsOnly", "Steamworks.Data.Lobby.SetPrivate", "Steamworks.Data.Lobby.SetJoinable",
    "Steamworks.Data.Lobby.SetData", "Steamworks.Data.Lobby.Leave"
    , "Netcode.Transports.Facepunch.FacepunchTransport.Steamworks.ISocketManager.OnConnecting"
    , "Netcode.Transports.Facepunch.FacepunchTransport.Steamworks.ISocketManager.OnConnected"
    , "Netcode.Transports.Facepunch.FacepunchTransport.Steamworks.ISocketManager.OnDisconnected"
    , "Netcode.Transports.Facepunch.FacepunchTransport.Steamworks.ISocketManager.OnMessage"
    , "Netcode.Transports.Facepunch.FacepunchTransport.DisconnectRemoteClient"
    , "Netcode.Transports.Facepunch.FacepunchTransport.Shutdown"
    , "Unity.Netcode.NetworkConnectionManager.HandleConnectionApproval"
};

IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
{
    foreach (var type in roots)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
    }
}

foreach (var type in AllTypes(plugin.Types))
{
    if (type.BaseType?.FullName == "Unity.Netcode.NetworkBehaviour")
        throw new Exception("Prototype must not add network behaviours.");
    foreach (var attribute in type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
    {
        TypeReference declaring;
        string methodName;
        if (attribute.ConstructorArguments.Count == 0 && type.Name == "ModerationApprovalPatch")
        {
            declaring = resolver.Resolve(plugin.AssemblyReferences.Single(a => a.Name == "Unity.Netcode.Runtime")).MainModule.Types.Single(t => t.FullName == "Unity.Netcode.NetworkConnectionManager");
            methodName = "HandleConnectionApproval";
            var selector = type.Methods.Single(m => m.Name == "TargetMethod");
            if (!selector.Body.Instructions.Any(i => i.Operand as string == methodName)) throw new Exception("Final approval selector changed.");
        }
        else
        {
            if (attribute.ConstructorArguments.Count != 2) throw new Exception("Unexpected patch declaration.");
            declaring = (TypeReference)attribute.ConstructorArguments[0].Value;
            methodName = (string)attribute.ConstructorArguments[1].Value;
        }
        string target = declaring.FullName + "." + methodName;
        if (!allowedTargets.Contains(target)) throw new Exception("Unexpected patch target: " + target);
        var originals = declaring.Resolve().Methods.Where(m => m.Name == methodName).ToArray();
        if (originals.Length != 1) throw new Exception("Missing or ambiguous patch target: " + target);
        var original = originals[0];
        foreach (var patch in type.Methods.Where(m => m.Name is "Prefix" or "Postfix" or "Finalizer"))
        {
            if (!patch.IsStatic) throw new Exception("Patch method is not static.");
            foreach (var parameter in patch.Parameters.Where(p => !p.Name.StartsWith("__")))
            {
                var originalParameter = original.Parameters.SingleOrDefault(p => p.Name == parameter.Name);
                string patchType = parameter.ParameterType is ByReferenceType byRef ? byRef.ElementType.FullName : parameter.ParameterType.FullName;
                if (originalParameter == null || originalParameter.ParameterType.FullName != patchType)
                    throw new Exception("Harmony parameter mismatch: " + target + " / " + parameter.Name);
            }
            foreach (var parameter in patch.Parameters.Where(p => p.Name == "__result"))
                if (parameter.ParameterType.GetElementType().FullName != original.ReturnType.FullName)
                    throw new Exception("Harmony result mismatch: " + target);
            foreach (var parameter in patch.Parameters.Where(p => p.Name.StartsWith("__") && int.TryParse(p.Name.Substring(2), out _)))
            {
                int position = int.Parse(parameter.Name.Substring(2));
                if (position >= original.Parameters.Count || (parameter.ParameterType is ByReferenceType positionalRef ? positionalRef.ElementType : parameter.ParameterType).FullName != original.Parameters[position].ParameterType.FullName)
                    throw new Exception("Harmony positional parameter mismatch: " + target);
            }
        }
        patches.Add(target + " " + original.MetadataToken);
        patchCount++;
    }
    foreach (var method in type.Methods.Where(m => m.HasBody))
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is MethodReference input && input.DeclaringType.FullName == "UnityEngine.Input")
                throw new Exception("Legacy Unity Input is disabled in this game; use Unity.InputSystem instead.");
            if (instruction.Operand is MethodReference member && member.DeclaringType.Scope is AssemblyNameReference scope &&
                (scope.Name is "Assembly-CSharp" or "BepInEx" or "0Harmony" or "Facepunch.Steamworks.Win64" or "Facepunch Transport for Netcode for GameObjects" or "Unity.Netcode.Runtime" or "Unity.InputSystem" || scope.Name.StartsWith("UnityEngine") || scope.Name == "Unity.TextMeshPro"))
            {
                if (member.Resolve() == null) throw new Exception("Unresolved external method: " + member.FullName);
                referencesChecked++;
            }
            if (instruction.Operand is FieldReference field && field.DeclaringType.Scope is AssemblyNameReference fieldScope &&
                (fieldScope.Name == "Assembly-CSharp" || fieldScope.Name.StartsWith("Unity")))
            {
                if (field.Resolve() == null) throw new Exception("Unresolved external field: " + field.FullName);
                bool approvalDenial = method.Name == "GuardNgoApproval" &&
                    field.DeclaringType.FullName == "Unity.Netcode.NetworkManager/ConnectionApprovalResponse" &&
                    (field.Name == "Reason" || (field.Name == "Approved" && instruction.Previous.OpCode == OpCodes.Ldc_I4_0));
                if ((instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld) && fieldScope.Name == "Unity.Netcode.Runtime" && !approvalDenial)
                    throw new Exception("Prototype must not write Netcode protocol/config fields.");
                referencesChecked++;
            }
        }
    }
}
if (patchCount != allowedTargets.Count) throw new Exception("Unexpected number of patches.");
var basePlugin = plugin.Types.Single(t => t.Name == "Plugin");
if (basePlugin.BaseType?.FullName != "BepInEx.BaseUnityPlugin" ||
    !basePlugin.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"))
    throw new Exception("BepInEx plugin metadata is missing.");
var awake = basePlugin.Methods.Single(m => m.Name == "Awake");
var configKey = awake.Body.Instructions.Single(i => i.Operand as string == "PublicHosting");
if (configKey.Next.OpCode != OpCodes.Ldc_I4_0) throw new Exception("Public Hosting must default OFF in the actual plugin configuration.");
bool Calls(MethodDefinition method, string declaring, string name) => method.HasBody && method.Body.Instructions.Any(i =>
    i.Operand is MethodReference m && m.DeclaringType.FullName == declaring && m.Name == name);
var validateAttempt = basePlugin.Methods.Single(m => m.Name == "ValidatePublicAttempt");
if (!Calls(validateAttempt, "RatGamePublicMatchmaking.JoinLease", "Revalidate") ||
    !Calls(validateAttempt, basePlugin.FullName, "ReadPublicSnapshot") || !Calls(validateAttempt, basePlugin.FullName, "TryLiveBuild"))
    throw new Exception("Public attempt validation is not wired to live build, metadata and scoped lease checks.");
var deferredGuard = basePlugin.Methods.Single(m => m.Name == "GuardDeferredConnection");
if (!deferredGuard.Body.Instructions.Any(i => i.Operand is MethodReference m &&
    m.DeclaringType.FullName == "RatGamePublicMatchmaking.JoinLease" && m.Name == "GuardDeferred" && m.Parameters.Count == 4))
    throw new Exception("Deferred public action is missing its authorization predicate.");
if (!deferredGuard.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldftn).Select(i => ((MethodReference)i.Operand).Resolve())
    .Any(m => Calls(m, basePlugin.FullName, "ValidatePublicAttempt")))
    throw new Exception("Deferred predicate is not wired to actual metadata authorization.");
if (!Calls(basePlugin.Methods.Single(m => m.Name == "Update"), basePlugin.FullName, "PollPublicJoin"))
    throw new Exception("Pending public joins are not polled for observed revocation.");
if (!Calls(basePlugin.Methods.Single(m => m.Name == "Update"), basePlugin.FullName, "PollHostLifecycle") ||
    !Calls(basePlugin.Methods.Single(m => m.Name == "Publish"), "RatGamePublicMatchmaking.Advertisement", "Publish") ||
    !Calls(basePlugin.Methods.Single(m => m.Name == "ReadPublicSnapshot"), "Steamworks.Data.Lobby", "GetData"))
    throw new Exception("Host lifecycle or snapshot validation wiring is absent.");
foreach (string key in new[] { "ratgame.state", "ratgame.joinable" })
    if (!basePlugin.Methods.Single(m => m.Name == "ReadPublicSnapshot").Body.Instructions.Any(i => i.Operand as string == key))
        throw new Exception("Public snapshots do not read " + key);
var queryStateMachine = AllTypes(plugin.Types).Single(t => t.Name.StartsWith("<RefreshPublic>d__", StringComparison.Ordinal));
foreach (string required in new[] { "ratgame.public", "ratgame.state", "waiting", "ratgame.joinable", "version" })
    if (!AllTypes(plugin.Types).SelectMany(t => t.Methods).Where(m => m.HasBody &&
        (m.DeclaringType == queryStateMachine || m.Name.Contains("<RefreshPublic>"))).Any(m => m.Body.Instructions.Any(i => i.Operand as string == required)))
        throw new Exception("Filtered discovery is missing " + required);
var hostStart = game.Types.Single(t => t.Name == "GameManager").Methods.Single(m => m.Name == "StartGameServerRpc");
if (!hostStart.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "CurrentGameState") ||
    !Calls(hostStart, "GameManager", "StartLoadLevelClientRpc")) throw new Exception("Native match start contract changed.");
var hostReturn = game.Types.Single(t => t.Name == "GameManager").Methods.Single(m => m.Name == "ReturnToLobby");
if (!Calls(hostReturn, "Unity.Netcode.NetworkSceneManager", "LoadScene")) throw new Exception("Native return-to-lobby load contract changed.");
var nativeStart = game.Types.Single(t => t.Name == "SteamNetworkManager").Methods.Single(m => m.Name == "StartClient");
var nativeAction = ((MethodReference)nativeStart.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldftn).Operand).Resolve();
if (!Calls(nativeStart, "TransitionScreen", "StartTransitionAsync") || !Calls(nativeAction, "Unity.Netcode.NetworkManager", "StartClient"))
    throw new Exception("The inspected native deferred StartClient chain changed.");
using (var ngo = ModuleDefinition.ReadModule(Path.Combine(managed, "Unity.Netcode.Runtime.dll"), parameters))
{
    var behaviour = ngo.Types.Single(t => t.FullName == "Unity.Netcode.NetworkBehaviour");
    if (!behaviour.Fields.Any(f => f.Name == "__rpc_exec_stage")) throw new Exception("RPC stage field changed.");
    var stage = behaviour.NestedTypes.Single(t => t.Name == "__RpcExecStage");
    if (!stage.Fields.Any(f => f.HasConstant && Convert.ToInt32(f.Constant) == 1)) throw new Exception("RPC execute stage changed.");
}
ModerationVerification.Run(plugin, game, managed, parameters);

var expected = new Dictionary<string, string>
{
    ["Assembly-CSharp.dll"] = "517DD87905AC3E2DCF845384C440293AEAAA82773F6977964AD459070A21E1C1",
    ["Facepunch.Steamworks.Win64.dll"] = "7F4E025564E9B2DD6AC248727B37C9767212C567B18A422629256737524D23B8",
    ["Facepunch Transport for Netcode for GameObjects.dll"] = "E44C50147AF3A3963BC05BE1C94CC2C1012B3F35DC0A5DC746C1744C58CCDB14",
    ["Unity.Netcode.Runtime.dll"] = "855BDA1F6148F3A82A09AA9D30BC956BBFC4A84F9B9BCCC0F56947A418154F5E"
};
foreach (var (file, hash) in expected)
{
    using var input = File.OpenRead(Path.Combine(managed, file));
    if (Convert.ToHexString(SHA256.HashData(input)) != hash) throw new Exception("Investigated game assembly changed: " + file);
}
string binDir = Path.GetDirectoryName(pluginPath)!;
if (Directory.GetFiles(binDir, "*.dll").Any(p => Path.GetFileName(p) != "RatGamePublicMatchmaking.dll"))
    throw new Exception("Build copied a dependency into the plugin output.");
string artifacts = Path.Combine(root, "artifacts");
Directory.CreateDirectory(artifacts);
using var pluginStream = File.OpenRead(pluginPath);
var report = new
{
    CheckedAtUtc = DateTime.UtcNow,
    PluginSHA256 = Convert.ToHexString(SHA256.HashData(pluginStream)),
    PluginInformationalVersion = (string)plugin.Assembly.CustomAttributes.Single(a => a.AttributeType.FullName == "System.Reflection.AssemblyInformationalVersionAttribute").ConstructorArguments[0].Value,
    GameAssemblyMvid = game.Mvid,
    GameAssembliesUnchanged = expected,
    ExternalMemberReferencesResolved = referencesChecked,
    PatchTargets = patches,
    OriginalAssembliesBundled = false,
    PublicHostingDefaultsOff = true,
    ScopedMetadataAndLiveBuildValidationWired = true,
    DeferredAuthorizationPredicatePresent = true,
    NativeDeferredStartClientChainVerified = true,
    ModerationTransportAndIdentityWiringVerified = true,
    PersistentBansUseAtomicLocalFile = true,
    LiveModerationTested = false,
    RuntimeGameExecuted = false,
    MultiplayerTested = false,
    NoReleasePackaged = true
};
File.WriteAllText(Path.Combine(artifacts, "Validation.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Validated {patchCount} exact Harmony targets and {referencesChecked} external member references against installed assemblies.");
Console.WriteLine("Original game hashes match the read-only investigation. No release package was created.");
Console.WriteLine("No game, Unity, Steam, authentication, or multiplayer code was executed.");

}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}




