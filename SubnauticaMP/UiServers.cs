using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SubnauticaMP
{
    /// <summary>Saved servers (name + address), kept in BepInEx/config/SubnauticaMP.servers.txt, one "name|address" per line.</summary>
    static class UiServers
    {
        public sealed class Entry { public string Name, Address; }

        public static readonly List<Entry> List = new List<Entry>();
        static bool _loaded;

        static string FilePath { get { return Path.Combine(BepInEx.Paths.ConfigPath, "SubnauticaMP.servers.txt"); } }

        static string Clean(string s) { return (s ?? "").Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        public static void Load(bool force = false)
        {
            if (_loaded && !force) return;
            _loaded = true;
            List.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int i = line.IndexOf('|');
                    string name = i < 0 ? "" : line.Substring(0, i), addr = i < 0 ? line : line.Substring(i + 1);
                    addr = Clean(addr);
                    if (addr.Length == 0) continue;
                    name = Clean(name);
                    List.Add(new Entry { Name = name.Length > 0 ? name : addr, Address = addr });
                }
            }
            catch (Exception e) { if (Plugin.I != null) Plugin.I.Log.Warn("could not read saved servers: " + e.Message); }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder("# Saved servers for Subnautica MP: name|address (ip, ip:port or steam:id)\n");
                foreach (var e in List) sb.Append(Clean(e.Name)).Append('|').Append(Clean(e.Address)).Append('\n');
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { if (Plugin.I != null) Plugin.I.Log.Warn("could not save servers: " + e.Message); }
        }

        /// <summary>Adds (index &lt; 0) or replaces an entry and saves.</summary>
        public static void Put(int index, string name, string address)
        {
            address = Clean(address); name = Clean(name);
            if (name.Length == 0) name = address;
            var e = new Entry { Name = name, Address = address };
            if (index >= 0 && index < List.Count) List[index] = e; else List.Add(e);
            Save();
        }

        public static void Remove(int index)
        {
            if (index < 0 || index >= List.Count) return;
            List.RemoveAt(index);
            Save();
        }
    }
}
