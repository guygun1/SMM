using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MPCore.IO;

namespace MPCore.Sync
{
    /// <summary>Mark a field or property for automatic replication. The owner writes it, everyone else reads it.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class SyncedAttribute : Attribute
    {
        /// <summary>Round floats to a multiple of this before comparing/sending. 0 = exact.</summary>
        public float Quantize;
    }

    public sealed class SyncField
    {
        public string Name;
        public Type ValueType;
        public ICodec Codec;
        public float Quantize;
        internal Func<object, object> Get;
        internal Action<object, object> Set;

        public void Encode(ByteWriter w, object target) { EncodeValue(w, Get(target)); }

        /// <summary>Read one value written by Encode and store it on the target.</summary>
        public void Decode(ByteReader r, object target) { Set(target, Codec.Read(r)); }

        public void EncodeValue(ByteWriter w, object v)
        {
            if (Quantize > 0f && v is float f) v = (float)Math.Round(f / Quantize) * Quantize;
            Codec.Write(w, v);
        }
    }

    public sealed class SyncType
    {
        public Type Type;
        public string Name;
        public uint Id;
        public string ModId;
        public SyncField[] Fields;
        public uint SchemaHash;
    }

    /// <summary>
    /// Which component types are synced and how. Mods opt in with [Synced] on their own fields, or a third party
    /// registers someone else's type by member name (config driven, no source access needed).
    /// </summary>
    public static class SyncRegistry
    {
        static readonly Dictionary<Type, SyncType> ByType = new Dictionary<Type, SyncType>();
        static readonly Dictionary<uint, SyncType> ById = new Dictionary<uint, SyncType>();
        static readonly object L = new object();

        public static IList<SyncType> All()
        {
            lock (L) return ByType.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        }

        public static void Clear() { lock (L) { ByType.Clear(); ById.Clear(); } }

        public static SyncType Register<T>() { return Register(typeof(T), null, null); }

        /// <param name="members">null = use members marked [Synced]. Otherwise exactly these names.</param>
        public static SyncType Register(Type t, string[] members, string modId)
        {
            lock (L)
            {
                SyncType existing;
                if (ByType.TryGetValue(t, out existing)) return existing;

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var found = new List<SyncField>();
                var seen = new HashSet<string>();
                for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                {
                    foreach (var m in cur.GetMembers(flags | BindingFlags.DeclaredOnly))
                    {
                        if (m.MemberType != MemberTypes.Field && m.MemberType != MemberTypes.Property) continue;
                        var attr = (SyncedAttribute)Attribute.GetCustomAttribute(m, typeof(SyncedAttribute));
                        bool pick = members == null ? attr != null : Array.IndexOf(members, m.Name) >= 0;
                        if (!pick || !seen.Add(m.Name)) continue;
                        found.Add(MakeField(t, m, attr != null ? attr.Quantize : 0f));
                    }
                }
                if (members != null)
                    foreach (var n in members)
                        if (!seen.Contains(n)) throw new ArgumentException(t.FullName + " has no field/property '" + n + "'");
                if (found.Count == 0) throw new ArgumentException(t.FullName + " has no [Synced] members");
                if (found.Count > 64) throw new ArgumentException(t.FullName + " has more than 64 synced members");

                found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var st = new SyncType
                {
                    Type = t,
                    Name = t.FullName,
                    Id = Hash.Str(t.FullName),
                    ModId = modId ?? t.Assembly.GetName().Name,
                    Fields = found.ToArray()
                };
                uint h = Hash.Str(st.Name);
                foreach (var f in st.Fields)
                    // invariant culture: the hash must be the same on every machine, whatever its locale decimal separator
                    h = Hash.Combine(h, Hash.Str(f.Name + ":" + f.ValueType.FullName + ":" + f.Quantize.ToString("R", CultureInfo.InvariantCulture)));
                st.SchemaHash = h;

                SyncType clash;
                if (ById.TryGetValue(st.Id, out clash))
                    throw new InvalidOperationException("type id hash clash: " + clash.Name + " vs " + st.Name);
                ByType[t] = st;
                ById[st.Id] = st;
                return st;
            }
        }

        /// <summary>Finds the registration for t or its nearest registered base class.</summary>
        public static bool TryGet(Type t, out SyncType st)
        {
            lock (L)
            {
                for (var cur = t; cur != null; cur = cur.BaseType)
                    if (ByType.TryGetValue(cur, out st)) return true;
            }
            st = null;
            return false;
        }

        public static bool TryGetById(uint id, out SyncType st) { lock (L) return ById.TryGetValue(id, out st); }

        static SyncField MakeField(Type owner, MemberInfo m, float quantize)
        {
            var f = new SyncField { Name = m.Name, Quantize = quantize };
            var fi = m as FieldInfo;
            var pi = m as PropertyInfo;
            if (fi != null)
            {
                f.ValueType = fi.FieldType;
                f.Get = o => fi.GetValue(o);
                f.Set = (o, v) => fi.SetValue(o, v);
            }
            else
            {
                if (!pi.CanRead || !pi.CanWrite) throw new ArgumentException(owner.FullName + "." + m.Name + " needs get and set");
                f.ValueType = pi.PropertyType;
                f.Get = o => pi.GetValue(o, null);
                f.Set = (o, v) => pi.SetValue(o, v, null);
            }
            ICodec c;
            if (!Codecs.TryGet(f.ValueType, out c))
                throw new ArgumentException(owner.FullName + "." + m.Name + ": no codec for " + f.ValueType.FullName +
                                            ". Call Codecs.Register<T>() first.");
            f.Codec = c;
            return f;
        }
    }
}
