using System;
using System.Collections;
using System.Collections.Generic;
using MPCore.IO;

namespace MPCore.Sync
{
    /// <summary>A reference to another networked object. Use this instead of GameObject/Component fields.</summary>
    public struct NetRef : IEquatable<NetRef>
    {
        public uint Id; // 0 = null
        public NetRef(uint id) { Id = id; }
        public bool IsNull => Id == 0;
        public bool Equals(NetRef o) => Id == o.Id;
        public override bool Equals(object o) => o is NetRef r && r.Id == Id;
        public override int GetHashCode() => (int)Id;
    }

    public interface ICodec
    {
        Type Type { get; }
        void Write(ByteWriter w, object v);
        object Read(ByteReader r);
    }

    sealed class DelegateCodec<T> : ICodec
    {
        readonly Action<ByteWriter, T> _w;
        readonly Func<ByteReader, T> _r;
        public DelegateCodec(Action<ByteWriter, T> w, Func<ByteReader, T> r) { _w = w; _r = r; }
        public Type Type => typeof(T);
        public void Write(ByteWriter w, object v) { _w(w, (T)v); }
        public object Read(ByteReader r) { return _r(r); }
    }

    sealed class EnumCodec : ICodec
    {
        readonly Type _t;
        public EnumCodec(Type t) { _t = t; }
        public Type Type => _t;
        public void Write(ByteWriter w, object v) { w.VarI(Convert.ToInt64(v)); }
        public object Read(ByteReader r) { return Enum.ToObject(_t, r.VarI()); }
    }

    sealed class ArrayCodec : ICodec
    {
        readonly Type _t; readonly Type _e; readonly ICodec _c;
        public ArrayCodec(Type t, ICodec c) { _t = t; _e = t.GetElementType(); _c = c; }
        public Type Type => _t;
        public void Write(ByteWriter w, object v)
        {
            var a = (Array)v;
            if (a == null) { w.VarU(0); return; }
            w.VarU((ulong)a.Length + 1);
            foreach (var x in a) _c.Write(w, x);
        }
        public object Read(ByteReader r)
        {
            ulong n = r.VarU();
            if (n == 0) return null;
            // every element takes at least one byte, so a count beyond what is left is corrupt: refuse before allocating
            if (n - 1 > (ulong)r.Remaining) throw new FormatException("array of " + (n - 1) + " elements cannot fit in " + r.Remaining + " bytes");
            var a = Array.CreateInstance(_e, (int)(n - 1));
            for (int i = 0; i < a.Length; i++) a.SetValue(_c.Read(r), i);
            return a;
        }
    }

    sealed class ListCodec : ICodec
    {
        readonly Type _t; readonly ICodec _c;
        public ListCodec(Type t, ICodec c) { _t = t; _c = c; }
        public Type Type => _t;
        public void Write(ByteWriter w, object v)
        {
            var l = (IList)v;
            if (l == null) { w.VarU(0); return; }
            w.VarU((ulong)l.Count + 1);
            foreach (var x in l) _c.Write(w, x);
        }
        public object Read(ByteReader r)
        {
            ulong n = r.VarU();
            if (n == 0) return null;
            // every element takes at least one byte, so a count beyond what is left is corrupt: refuse before allocating
            if (n - 1 > (ulong)r.Remaining) throw new FormatException("list of " + (n - 1) + " elements cannot fit in " + r.Remaining + " bytes");
            var l = (IList)Activator.CreateInstance(_t);
            for (ulong i = 0; i + 1 < n; i++) l.Add(_c.Read(r));
            return l;
        }
    }

    public static class Codecs
    {
        static readonly Dictionary<Type, ICodec> Map = new Dictionary<Type, ICodec>();

        static Codecs()
        {
            Register<bool>((w, v) => w.Bool(v), r => r.Bool());
            Register<byte>((w, v) => w.U8(v), r => r.U8());
            Register<sbyte>((w, v) => w.VarI(v), r => (sbyte)r.VarI());
            Register<short>((w, v) => w.VarI(v), r => (short)r.VarI());
            Register<ushort>((w, v) => w.VarU(v), r => (ushort)r.VarU());
            Register<int>((w, v) => w.VarI(v), r => (int)r.VarI());
            Register<uint>((w, v) => w.VarU(v), r => (uint)r.VarU());
            Register<long>((w, v) => w.VarI(v), r => r.VarI());
            Register<ulong>((w, v) => w.VarU(v), r => r.VarU());
            Register<float>((w, v) => w.F32(v), r => r.F32());
            Register<double>((w, v) => w.F64(v), r => r.F64());
            Register<string>((w, v) => w.Str(v), r => r.Str());
            Register<byte[]>((w, v) => w.Bytes(v), r => r.Bytes());
            Register<NetRef>((w, v) => w.VarU(v.Id), r => new NetRef((uint)r.VarU()));
        }

        /// <summary>Teach the system a new type (e.g. Vector3). Safe to call again to replace.</summary>
        public static void Register<T>(Action<ByteWriter, T> write, Func<ByteReader, T> read)
        {
            lock (Map) Map[typeof(T)] = new DelegateCodec<T>(write, read);
        }

        public static bool TryGet(Type t, out ICodec codec)
        {
            lock (Map)
            {
                if (Map.TryGetValue(t, out codec)) return true;
                if (t.IsEnum) { codec = new EnumCodec(t); Map[t] = codec; return true; }
                if (t.IsArray && t.GetArrayRank() == 1)
                {
                    ICodec e;
                    if (TryGet(t.GetElementType(), out e)) { codec = new ArrayCodec(t, e); Map[t] = codec; return true; }
                }
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    ICodec e;
                    if (TryGet(t.GetGenericArguments()[0], out e)) { codec = new ListCodec(t, e); Map[t] = codec; return true; }
                }
            }
            codec = null;
            return false;
        }
    }
}
