using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RatGamePublicMatchmaking
{
    internal interface IBanStorage
    {
        string? Read();
        void Commit(string data);
    }

    internal sealed class AtomicBanFile : IBanStorage
    {
        private readonly string path;
        internal AtomicBanFile(string path) { this.path = Path.GetFullPath(path); }
        public string? Read()
        {
            if (!File.Exists(path))
            {
                if (File.Exists(path + ".bak")) throw new IOException("Main banlist is missing but a backup exists. Restore it before hosting.");
                return null;
            }
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("Banlist exceeds the supported size.");
            return File.ReadAllText(path, Encoding.UTF8);
        }
        public void Commit(string data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(data);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class BanStore
    {
        private readonly object gate = new object();
        private readonly IBanStorage storage;
        private Dictionary<ulong, string> entries = new Dictionary<ulong, string>();
        internal bool Ready { get; private set; }
        internal string Error { get; private set; } = "";
        internal BanStore(IBanStorage storage)
        {
            this.storage = storage;
            try { entries = Parse(storage.Read()); Ready = true; }
            catch (Exception error) { Error = "Banlist unavailable (" + error.GetType().Name + "). Repair the file and restart; remote admission is blocked."; }
        }
        internal KeyValuePair<ulong, string>[] Snapshot() { lock (gate) return entries.OrderBy(p => p.Key).ToArray(); }
        internal bool Allows(ulong steam) { lock (gate) return Ready && SteamAccount.Valid(steam) && !entries.ContainsKey(steam); }
        internal bool Add(ulong steam, string name)
        {
            lock (gate)
            {
                if (!Ready || !SteamAccount.Valid(steam)) return false;
                if (entries.ContainsKey(steam)) return true;
                var next = new Dictionary<ulong, string>(entries) { [steam] = DisplayName(name) };
                return Save(next);
            }
        }
        internal bool Remove(ulong steam)
        {
            lock (gate)
            {
                if (!Ready || !SteamAccount.Valid(steam)) return false;
                if (!entries.ContainsKey(steam)) return true;
                var next = new Dictionary<ulong, string>(entries);
                next.Remove(steam);
                return Save(next);
            }
        }
        private bool Save(Dictionary<ulong, string> next)
        {
            try
            {
                var root = new XElement("HostBans", new XAttribute("version", "1"), next.OrderBy(p => p.Key).Select(p =>
                    new XElement("Account", new XAttribute("steamId", p.Key.ToString(CultureInfo.InvariantCulture)), new XAttribute("name", p.Value))));
                if (root.ToString().Length > 4 * 1024 * 1024) throw new IOException("Banlist exceeds the supported size.");
                storage.Commit(root.ToString());
                entries = next;
                Error = "";
                return true;
            }
            catch (Exception error) { Error = "Banlist change was not saved (" + error.GetType().Name + "). Existing bans remain active."; return false; }
        }
        private static Dictionary<ulong, string> Parse(string? data)
        {
            var result = new Dictionary<ulong, string>();
            if (data == null) return result;
            using (var reader = XmlReader.Create(new StringReader(data), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 }))
            {
                var root = XElement.Load(reader);
                if (root.Name != "HostBans" || (string?)root.Attribute("version") != "1" || root.Attributes().Count() != 1)
                    throw new FormatException("Unsupported banlist schema.");
                foreach (var row in root.Nodes())
                {
                    if (row is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) continue;
                    if (!(row is XElement element) || element.Name != "Account" || element.HasElements ||
                        !string.IsNullOrWhiteSpace(element.Value) || element.Attributes().Any(a => a.Name != "steamId" && a.Name != "name"))
                        throw new FormatException("Invalid ban entry.");
                    string id = (string?)element.Attribute("steamId") ?? "";
                    if (id.Length != 17 || !ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steam) || !SteamAccount.Valid(steam))
                        throw new FormatException("Invalid Steam account.");
                    string name = (string?)element.Attribute("name") ?? "Steam account";
                    result[steam] = DisplayName(name);
                }
            }
            return result;
        }

        internal static string DisplayName(string? name)
        {
            var safe = new StringBuilder();
            if (name != null)
                for (int i = 0; i < name.Length && safe.Length < 80; i++)
                {
                    char c = name[i];
                    if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                    {
                        if (safe.Length + 2 > 80) break;
                        safe.Append(c).Append(name[++i]);
                    }
                    else if (XmlConvert.IsXmlChar(c) && !char.IsControl(c)) safe.Append(c);
                }
            return string.IsNullOrWhiteSpace(safe.ToString()) ? "Steam account" : safe.ToString();
        }
    }
}
