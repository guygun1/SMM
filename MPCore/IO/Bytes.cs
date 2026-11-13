using System;
using System.Text;

namespace MPCore.IO
{
    /// <summary>Stable FNV-1a hashes. Same on every peer, independent of load order.</summary>
    public static class Hash
    {
        public static uint Str(string s)
        {
            uint h = 2166136261;
            if (s == null) return h;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h;
        }

        public static uint Bytes(byte[] b)
        {
            uint h = 2166136261;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619; }
            return h;
        }

        public static uint Combine(uint a, uint b) => (a ^ b) * 16777619 + 0x9E3779B9;
    }

    public sealed class ByteWriter
    {
        byte[] _b;
        int _n;

        public ByteWriter(int cap = 128) { _b = new byte[cap]; }
        public int Length => _n;
        public void Clear() { _n = 0; }

        void Need(int k)
        {
            if (_n + k > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + k));
        }

        public void U8(byte v) { Need(1); _b[_n++] = v; }
        public void Bool(bool v) { U8(v ? (byte)1 : (byte)0); }

        public void VarU(ulong v)
        {
            while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; }
            U8((byte)v);
        }

        public void VarI(long v) { VarU((ulong)((v << 1) ^ (v >> 63))); }

        public void F32(float v) { Raw(BitConverter.GetBytes(v), 0, 4); }
        public void F64(double v) { Raw(BitConverter.GetBytes(v), 0, 8); }

        public void Str(string s)
        {
            if (s == null) { VarU(0); return; }
            var x = Encoding.UTF8.GetBytes(s);
            VarU((ulong)x.Length + 1);
            Raw(x, 0, x.Length);
        }

        public void Raw(byte[] src, int off, int len)
        {
            Need(len);
            Buffer.BlockCopy(src, off, _b, _n, len);
            _n += len;
        }

        public void Bytes(byte[] a)
        {
            if (a == null) { VarU(0); return; }
            VarU((ulong)a.Length + 1);
            Raw(a, 0, a.Length);
        }

        /// <summary>True if the written bytes equal other. No allocation: used for per-tick change detection.</summary>
        public bool SameAs(byte[] other)
        {
            if (other == null || other.Length != _n) return false;
            for (int i = 0; i < _n; i++) if (_b[i] != other[i]) return false;
            return true;
        }

        public byte[] ToArray()
        {
            var r = new byte[_n];
            Buffer.BlockCopy(_b, 0, r, 0, _n);
            return r;
        }
    }

    public sealed class ByteReader
    {
        readonly byte[] _b;
        int _p;
        readonly int _end;

        public ByteReader(byte[] b) : this(b, 0, b.Length) { }
        public ByteReader(byte[] b, int off, int len) { _b = b; _p = off; _end = off + len; }

        public int Remaining => _end - _p;

        /// <summary>Absolute index of the next byte in the underlying array.</summary>
        public int Position => _p;

        void Need(int k)
        {
            // k < 0 guards against a negative length; compare with subtraction so _p + k cannot overflow.
            if (k < 0 || k > _end - _p) throw new FormatException("read past end of message");
        }

        /// <summary>Length prefix of a Str or Bytes field: n is length + 1 and n != 0. Validated as ulong before any int cast.</summary>
        int LengthPrefix(ulong n)
        {
            if (n - 1 > (ulong)Remaining) throw new FormatException("length " + (n - 1) + " past end of message");
            return (int)(n - 1);
        }

        public byte U8() { Need(1); return _b[_p++]; }
        public bool Bool() { return U8() != 0; }

        public ulong VarU()
        {
            ulong r = 0; int shift = 0;
            while (true)
            {
                byte b = U8();
                r |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return r;
                shift += 7;
                if (shift > 63) throw new FormatException("varint too long");
            }
        }

        public long VarI() { ulong u = VarU(); return (long)(u >> 1) ^ -(long)(u & 1); }

        public float F32() { Need(4); var v = BitConverter.ToSingle(_b, _p); _p += 4; return v; }
        public double F64() { Need(8); var v = BitConverter.ToDouble(_b, _p); _p += 8; return v; }

        public string Str()
        {
            ulong n = VarU();
            if (n == 0) return null;
            int len = LengthPrefix(n);
            Need(len);
            var s = Encoding.UTF8.GetString(_b, _p, len);
            _p += len;
            return s;
        }

        public byte[] Bytes()
        {
            ulong n = VarU();
            if (n == 0) return null;
            int len = LengthPrefix(n);
            Need(len);
            var r = new byte[len];
            Buffer.BlockCopy(_b, _p, r, 0, len);
            _p += len;
            return r;
        }

        public byte[] Rest()
        {
            var r = new byte[Remaining];
            Buffer.BlockCopy(_b, _p, r, 0, r.Length);
            _p = _end;
            return r;
        }
    }
}
