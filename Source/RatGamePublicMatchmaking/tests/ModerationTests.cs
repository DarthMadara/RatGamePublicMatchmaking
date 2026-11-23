using RatGamePublicMatchmaking;

internal static class ModerationTests
{
    private const ulong Host = 76561198000000001, Alice = 76561198000000002, Bob = 76561198000000003;
    internal static void Run(Action<bool, string> check)
    {
        var storage = new MemoryStorage();
        var bans = new BanStore(storage);
        var registry = new SteamConnections();
        var session = new Session();
        var moderation = new HostModerator(session, bans);
        var alice = session.Connect(42, 700, Alice, "Same name");
        var bob = session.Connect(43, 800, Bob, "Same name");
        check(alice.ClientId != alice.TransportId && alice.SteamId != alice.ClientId, "moderation IDs occupy three distinct domains");
        check(moderation.Kick(alice) == ModerationResult.Requested && !session.Peers.ContainsKey(42), "offline host kick removes selected connected client");
        check(session.Peers.ContainsKey(43) && session.IsHost && session.HostConnected, "offline kick leaves other client and host connected");
        check(bans.Allows(Alice) && bans.Snapshot().Length == 0 && storage.Writes == 0, "kick does not create or persist a ban");
        alice = session.Connect(44, 900, Alice, "Renamed");
        check(bans.Allows(Alice) && moderation.Kick(alice) == ModerationResult.Requested, "kicked account may reconnect and be managed again");
        alice = session.Connect(45, 1000, Alice, "Alice");
        check(moderation.Ban(alice, false) == ModerationResult.ConfirmationRequired && bans.Allows(Alice) && session.Peers.ContainsKey(45),
            "unconfirmed ban changes neither file nor connection");
        bool durableBeforeDisconnect = false;
        session.BeforeDisconnect = peer => durableBeforeDisconnect = !bans.Allows(peer.SteamId) && new BanStore(storage).Snapshot().Length == 1;
        check(moderation.Ban(alice, true) == ModerationResult.Requested && !session.Peers.ContainsKey(45), "offline confirmed ban disconnects selected client");
        session.BeforeDisconnect = null;
        check(durableBeforeDisconnect, "durable ban and admission rejection exist before disconnect");
        check(!bans.Allows(Alice) && bans.Allows(Bob) && session.Peers.ContainsKey(43) && session.HostConnected,
            "banned rejoin is rejected while non-banned peer and host remain intact");
        var restarted = new BanStore(storage);
        check(!restarted.Allows(Alice) && restarted.Snapshot()[0].Value == "Alice", "reloaded store preserves Steam ID and last-known name");
        int writes = storage.Writes;
        check(bans.Add(Alice, "Changed") && bans.Snapshot().Length == 1 && storage.Writes == writes, "duplicate bans do not duplicate entries or write unnecessarily");
        check(moderation.Unban(Alice) && bans.Allows(Alice) && new BanStore(storage).Allows(Alice), "unban persists and permits reconnect");

        foreach (ulong invalid in new[] { 0UL, 1UL, ulong.MaxValue, 76561197960265728UL, 103582791429521412UL, 76561198000000002UL + (1UL << 56) })
            check(!SteamAccount.Valid(invalid) && !bans.Add(invalid, "invalid") && !bans.Allows(invalid), "invalid Steam ID rejected: " + invalid);

        alice = session.Connect(46, 1100, Alice, "Alice");
        session.IsHost = false;
        check(moderation.Kick(alice) == ModerationResult.NotHost, "non-host cannot kick");
        check(moderation.Ban(alice, true) == ModerationResult.NotHost && bans.Allows(Alice), "non-host cannot ban");
        check(!moderation.Unban(Bob), "non-host cannot unban");
        session.IsHost = true;
        var self = new ModerationPeer(session.HostClientId, 0, Host, 1, "Host");
        check(moderation.Kick(self) == ModerationResult.Self && session.HostConnected, "host cannot kick own client");
        check(moderation.Ban(self, true) == ModerationResult.Self && bans.Allows(Host), "host cannot ban own client");
        check(moderation.Kick(new ModerationPeer(999, 1200, Host, 1, "spoof")) == ModerationResult.Self,
            "host account cannot be targeted under another client ID");
        check(moderation.Ban(new ModerationPeer(999, 1200, 0, 1, "spoof"), true) == ModerationResult.InvalidIdentity,
            "missing Steam identity cannot be banned");
        check(moderation.Kick(new ModerationPeer(ulong.MaxValue, 1200, Alice, 1, "spoof")) == ModerationResult.StalePlayer,
            "invalid NGO client ID cannot cause a disconnect");
        check(moderation.Ban(new ModerationPeer(alice.ClientId, bob.TransportId, Bob, alice.Generation, "Alice"), true) == ModerationResult.StalePlayer && bans.Allows(Bob),
            "spoofed client/Steam mapping cannot moderate another account");
        check(moderation.Ban(new ModerationPeer(alice.ClientId, alice.TransportId, Alice, alice.Generation + 1, "Alice"), true) == ModerationResult.StalePlayer,
            "forged generation cannot authorize a moderation action");
        session.Peers.Remove(alice.ClientId);
        check(moderation.Kick(alice) == ModerationResult.StalePlayer && moderation.Ban(alice, true) == ModerationResult.StalePlayer,
            "disconnected selection is rejected for kick and confirmation");
        var replacement = session.Connect(alice.ClientId, alice.TransportId, Alice, "Alice");
        check(moderation.Ban(alice, true) == ModerationResult.StalePlayer && session.Peers.ContainsKey(replacement.ClientId),
            "recycled handle and client ID do not reuse an old ban confirmation");
        session.FailDisconnect = true;
        check(moderation.Kick(replacement) == ModerationResult.DisconnectFailed && bans.Allows(Alice), "kick failure is reported without banning or shutting host down");
        check(moderation.Ban(replacement, true) == ModerationResult.BannedDisconnectFailed && !bans.Allows(Alice) && !new BanStore(storage).Allows(Alice),
            "disconnect failure retains durable ban and blocks reconnect");
        check(session.HostConnected && session.Peers.ContainsKey(bob.ClientId), "disconnect failure preserves other peers and host");
        moderation.Unban(Alice);
        session.ThrowDisconnect = true;
        check(moderation.Kick(replacement) == ModerationResult.DisconnectFailed, "disconnect exception is contained");
        session.ThrowDisconnect = false; session.FailDisconnect = false;
        storage.FailWrite = true;
        check(moderation.Ban(replacement, true) == ModerationResult.StorageFailed && bans.Allows(Alice) && session.Peers.ContainsKey(replacement.ClientId),
            "persistence failure does not pretend to ban or disconnect");
        storage.FailWrite = false;
        bans.Add(Alice, "Alice"); storage.FailWrite = true;
        check(!moderation.Unban(Alice) && !bans.Allows(Alice) && !new BanStore(storage).Allows(Alice), "failed unban keeps the original ban active and durable");
        storage.FailWrite = false; moderation.Unban(Alice);
        storage.AfterWrite = () => { session.IsHost = false; storage.AfterWrite = null; };
        check(moderation.Ban(replacement, true) == ModerationResult.BannedDisconnectFailed && session.Peers.ContainsKey(replacement.ClientId),
            "authority lost during persistence prevents stale disconnect while retaining ban");
        session.IsHost = true; moderation.Unban(Alice);

        check(registry.Bind(1234, Alice) && registry.TryGet(1234, out var first) && first.SteamId == Alice, "SDK transport handle binds to validated Steam account");
        registry.TryGet(1234, out first);
        check(!registry.Bind(1234, Bob), "an active transport handle cannot change Steam account");
        check(registry.AllowsMessage(1234, Alice, bans) && !registry.AllowsMessage(1234, Bob, bans) && !registry.AllowsMessage(9999, Alice, bans),
            "actual incoming gate rejects identity mismatch and unknown connections");
        bans.Add(Alice, "Alice");
        check(!registry.AllowsMessage(1234, Alice, bans), "actual incoming gate rejects banned traffic during pending approval and after close failure");
        moderation.Unban(Alice);
        registry.Forget(1234);
        check(!registry.AllowsMessage(1234, Alice, bans), "kick cleanup prevents late packets from retaining admission");
        check(registry.Bind(1234, Alice) && registry.TryGet(1234, out var second) && second.Generation != first.Generation,
            "kicked or disconnected account may reconnect with a new connection generation");
        registry.TryGet(1234, out second);
        registry.Clear(); registry.Bind(1234, Alice); registry.TryGet(1234, out var third);
        check(third.Generation != second.Generation, "host shutdown cleanup never resets the generation counter");
        check(!registry.Bind(0, Alice) && !registry.Bind(ulong.MaxValue, Alice) && !registry.Bind(1, 0), "invalid connection handles and IDs cannot enter mapping");
        check(registry.AllowsAdmission(1234, bans) && !registry.AllowsAdmission(9999, bans), "final approval requires an existing authenticated connection mapping");
        bans.Add(Alice, "Alice");
        check(!registry.AllowsAdmission(1234, bans), "a request queued before ban persistence is rejected at final approval without another packet");
        moderation.Unban(Alice);
        check(registry.AllowsAdmission(1234, bans), "unban restores final approval eligibility without authorizing unknown identities");

        const string validRow = "<Account steamId='76561198000000002' name='Alice'/>";
        var duplicate = new BanStore(new MemoryStorage { Data = "<HostBans version='1'>" + validRow + validRow + "</HostBans>" });
        check(duplicate.Ready && duplicate.Snapshot().Length == 1, "duplicate disk entries load as one account");
        var missingName = new BanStore(new MemoryStorage { Data = "<HostBans version='1'><Account steamId='76561198000000002'/></HostBans>" });
        check(missingName.Ready && !missingName.Allows(Alice), "missing display name does not erase account ban");
        foreach (string malformed in new[] { "", "{broken", "<HostBans version='2'/>", "<HostBans version='1'><Account steamId='0'/></HostBans>",
            "<HostBans version='1'><Account steamId=' 76561198000000002'/></HostBans>", "<HostBans version='1'><Unknown/></HostBans>",
            "<HostBans version='1'><Account steamId='76561198000000002'><Nested/></Account></HostBans>",
            "<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///private'>]><HostBans version='1'>&ext;</HostBans>" })
        {
            var damagedStorage = new MemoryStorage { Data = malformed };
            var damaged = new BanStore(damagedStorage);
            check(!damaged.Ready && !damaged.Allows(Alice) && !damaged.Add(Bob, "Bob") && !damaged.Remove(Alice) && damagedStorage.Writes == 0 && damagedStorage.Data == malformed,
                "malformed/unsupported banlist fails closed and is preserved: " + malformed[..Math.Min(30, malformed.Length)]);
        }
        var unreadable = new BanStore(new MemoryStorage { FailRead = true });
        check(!unreadable.Ready && !unreadable.Allows(Alice), "unreadable storage cannot silently reset bans");
        var names = new BanStore(new MemoryStorage());
        check(names.Add(Alice, "<tag>&\nAlice\t") && names.Snapshot()[0].Value == "<tag>&Alice", "names remain bounded plain labels with XML escaping");
        check(names.Add(Bob, new string('x', 200)) && names.Snapshot().Single(p => p.Key == Bob).Value.Length == 80,
            "stored names have a fixed length bound");
        foreach (string unusual in new[] { new string('x', 79) + "\U0001F400", "\ud800Alice\udfff", "\ufffe\uffffAlice", "\n\t", "\U0001F400Alice" })
        {
            var unusualStorage = new MemoryStorage();
            var unusualNames = new BanStore(unusualStorage);
            check(unusualNames.Add(Alice, unusual) && new BanStore(unusualStorage).Ready && !new BanStore(unusualStorage).Allows(Alice),
                "malformed or truncated Unicode name cannot prevent a durable account ban");
        }
        check(BanStore.DisplayName("\U0001F400Alice") == "\U0001F400Alice" && BanStore.DisplayName(new string('x', 79) + "\U0001F400").Length == 79,
            "Unicode names preserve whole pairs at the display length boundary");
        AtomicPersistence(check);
    }

    private static void AtomicPersistence(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "RGPM-ModerationTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "bans.v1.xml");
            var store = new BanStore(new AtomicBanFile(file));
            check(store.Ready && store.Allows(Alice) && !File.Exists(file), "new installation with no banlist starts empty without writing a reset");
            check(store.Add(Alice, "Alice"), "first real filesystem commit succeeds");
            check(!new BanStore(new AtomicBanFile(file)).Allows(Alice), "real filesystem restart reload retains ban");
            check(store.Add(Bob, "Bob") && File.Exists(file + ".bak") && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "atomic replacement keeps backup and cleans temporary files");
            string prior = File.ReadAllText(file);
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                check(!store.Remove(Alice) && !store.Allows(Alice), "locked real ban file prevents unban without mutating memory");
            check(File.ReadAllText(file) == prior && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "failed atomic replacement preserves original file and removes incomplete temporary data");
            check(store.Remove(Alice) && new BanStore(new AtomicBanFile(file)).Allows(Alice) && !new BanStore(new AtomicBanFile(file)).Allows(Bob),
                "real file unban preserves every other ban across restart");
            File.WriteAllText(file, "broken");
            var corrupt = new BanStore(new AtomicBanFile(file));
            check(!corrupt.Ready && !corrupt.Remove(Bob) && File.ReadAllText(file) == "broken" && File.Exists(file + ".bak"),
                "corrupt file and backup are preserved for recovery instead of auto-reset");
            File.Delete(file);
            check(!new BanStore(new AtomicBanFile(file)).Ready, "missing main file with backup requires explicit recovery");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MemoryStorage : IBanStorage
    {
        internal string? Data;
        internal int Writes;
        internal bool FailRead, FailWrite;
        internal Action? AfterWrite;
        public string? Read() => FailRead ? throw new IOException("read failed") : Data;
        public void Commit(string data)
        {
            if (FailWrite) throw new IOException("disk full");
            Data = data; Writes++; AfterWrite?.Invoke();
        }
    }
    private sealed class Session : IModerationSession
    {
        public bool IsHost { get; set; } = true;
        public ulong HostClientId => 0;
        public ulong HostSteamId => Host;
        internal bool HostConnected = true, FailDisconnect, ThrowDisconnect;
        internal Dictionary<ulong, ModerationPeer> Peers = new();
        internal Action<ModerationPeer>? BeforeDisconnect;
        private long nextGeneration;
        internal ModerationPeer Connect(ulong client, ulong handle, ulong steam, string name)
        {
            var peer = new ModerationPeer(client, handle, steam, ++nextGeneration, name);
            Peers[client] = peer;
            return peer;
        }
        public bool Resolve(ulong client, out ModerationPeer peer) => Peers.TryGetValue(client, out peer);
        public bool Disconnect(ModerationPeer peer)
        {
            BeforeDisconnect?.Invoke(peer);
            if (ThrowDisconnect) throw new IOException("disconnect failed");
            return !FailDisconnect && Peers.Remove(peer.ClientId);
        }
    }
}
