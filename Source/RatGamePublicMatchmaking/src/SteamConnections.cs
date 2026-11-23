using System.Collections.Generic;

namespace RatGamePublicMatchmaking
{
    // One registry per live transport session. Values originate only in SDK callbacks.
    internal sealed class SteamConnections
    {
        private readonly Dictionary<ulong, Binding> connections = new Dictionary<ulong, Binding>();
        private long nextGeneration;
        internal readonly struct Binding
        {
            internal readonly ulong SteamId;
            internal readonly long Generation;
            internal Binding(ulong steam, long generation) { SteamId = steam; Generation = generation; }
        }
        internal bool Bind(ulong handle, ulong steam)
        {
            if (handle == 0 || handle > uint.MaxValue || !SteamAccount.Valid(steam)) return false;
            if (connections.TryGetValue(handle, out var existing)) return existing.SteamId == steam;
            connections.Add(handle, new Binding(steam, ++nextGeneration));
            return true;
        }
        internal bool TryGet(ulong handle, out Binding binding) => connections.TryGetValue(handle, out binding);
        internal bool AllowsMessage(ulong handle, ulong sdkSteamId, BanStore bans) =>
            connections.TryGetValue(handle, out var binding) && binding.SteamId == sdkSteamId && bans.Allows(binding.SteamId);
        internal bool AllowsAdmission(ulong handle, BanStore bans) =>
            connections.TryGetValue(handle, out var binding) && bans.Allows(binding.SteamId);
        internal void Forget(ulong handle) => connections.Remove(handle);
        internal void Clear() => connections.Clear(); // Generation intentionally never resets.
    }
}
