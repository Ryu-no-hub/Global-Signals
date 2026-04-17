using System;
using System.Text;

namespace SERingAsteroids.OctreeStorage
{
    public class ByteArrayBuffer
    {
        private byte[] Buffer;
        private readonly int Offset;

        public int Length { get; private set; }

        public int Position { get; private set; }

        public ByteArrayBuffer(int capacity)
        {
            Buffer = new byte[capacity];
            Length = 0;
            Position = 0;
            Offset = 0;
        }

        public ByteArrayBuffer() : this(4096)
        {
        }

        public ByteArrayBuffer(byte[] buffer)
        {
            Buffer = buffer;
            Length = buffer.Length;
            Position = 0;
            Offset = 0;
        }

        public ByteArrayBuffer(byte[] buffer, int offset, int count)
        {
            Buffer = buffer;
            Offset = offset;
            Length = count;
            Position = 0;
        }

        public byte this[int index]
        {
            get
            {
                if (index > Length) throw new NotSupportedException();
                return Buffer[Offset + index];
            }
        }

        public byte[] ToArray()
        {
            if (Offset == 0 && Buffer.Length == Length)
            {
                return Buffer;
            }

            var retarray = new byte[Length];
            Array.Copy(Buffer, Offset, retarray, 0, Length);
            return retarray;
        }

        public bool TryRead(int count, out ByteArrayBuffer slice)
        {
            slice = null;
            if (Position + count > Length) return false;
            slice = new ByteArrayBuffer(Buffer, Offset + Position, count);
            Position += count;
            return true;
        }

        public bool TryRead(int count, ByteArrayBuffer buffer)
        {
            if (Position + count > Length) return false;

            if (ReferenceEquals(Buffer, buffer.Buffer))
            {
                buffer.Length += count;
            }
            else
            {
                buffer.Write(Buffer, Offset + Position, count);
            }

            Position += count;
            return true;
        }

        private void Reserve(int count)
        {
            var size = Offset + Position + count;

            if (size > Buffer.Length)
            {
                if (size > Buffer.Length * 2)
                {
                    Array.Resize(ref Buffer, size + 4096);
                }
                else
                {
                    Array.Resize(ref Buffer, Buffer.Length * 2);
                }
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            Reserve(count);
            Array.Copy(buffer, offset, Buffer, Offset + Position, count);
            Position += count;

            if (Length < Position)
            {
                Length = Position;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (count > Length - Position)
            {
                count = Length - Position;
            }

            Array.Copy(Buffer, Offset + Position, buffer, offset, count);
            Position += count;
            return count;
        }

        public bool TryRead(byte[] buffer, int offset, int count)
        {
            if (count > Length - Position)
            {
                return false;
            }

            Array.Copy(Buffer, Offset + Position, buffer, offset, count);
            Position += count;
            return true;
        }

        private bool TryReadValue<T>(out T val, Func<byte[], int, T> readFunc)
            where T : struct
        {
            var size = SizeOf<T>();
            val = default(T);

            if (Position + size > Length) return false;

            val = readFunc(Buffer, Offset + Position);
            Position += size;
            return true;
        }

        private bool TryReadValue<T>(out T val, int size, Func<byte[], int, int, T> readFunc)
        {
            val = default(T);

            if (Position + size > Length) return false;

            val = readFunc(Buffer, Offset + Position, size);
            Position += size;
            return true;
        }

        private void WriteValue<T>(T val, Action<T, byte[], int> writeFunc)
            where T : struct
        {
            var size = SizeOf<T>();
            WriteValue(val, size, writeFunc);
        }

        private void WriteValue<T>(T val, int size, Action<T, byte[], int> writeFunc)
        {
            Reserve(size);
            writeFunc(val, Buffer, Offset + Position);
            Position += size;

            if (Length < Position)
            {
                Length = Position;
            }
        }

        private void WriteValue<T>(T val, Func<T, byte[]> convertFunc)
            where T : struct
        {
            WriteValue(val, (v, b, o) => Array.Copy(convertFunc(v), 0, b, o, SizeOf<T>()));
        }

        public static int SizeOf7BitEncodedInt(uint val)
        {
            if (val < 0x80) return 1;
            if (val < 0x4000) return 2;
            if (val < 0x200000) return 3;
            if (val < 0x10000000) return 4;
            return 5;
        }

        public static int SizeOf<T>() where T : struct
        {
            var type = typeof(T);
            if (type == typeof(byte) || type == typeof(sbyte)) return 1;
            if (type == typeof(short) || type == typeof(ushort)) return 2;
            if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
            if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
            throw new NotSupportedException();
        }

        public static int SizeOf<T>(T _) where T : struct => SizeOf<T>();

        public static int SizeOf(string str)
        {
            var size = Encoding.UTF8.GetByteCount(str);
            return SizeOf7BitEncodedInt((uint)size) + size;
        }

        public void Write7BitEncodedInt(uint val)
        {
            var len = SizeOf7BitEncodedInt(val);
            Reserve(len);

            while (val >= 128)
            {
                Buffer[Offset + Position++] = (byte)((val & 0x7F) | 0x80);
                val >>= 7;
            }

            Buffer[Offset + Position++] = (byte)val;

            if (Length < Position)
            {
                Length = Position;
            }
        }

        public bool TryRead7BitEncodedInt(out uint val)
        {
            byte b;
            var shift = 0;
            val = 0;

            do
            {
                if (shift > 32)
                    return false;

                if (!TryRead(out b))
                    return false;

                val |= (uint)(b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0);

            return true;
        }

        public void Write(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                Write((byte)0);
                return;
            }

            var len = Encoding.UTF8.GetByteCount(str);
            Write7BitEncodedInt((uint)len);
            WriteValue(str, len, (s, b, o) => Encoding.UTF8.GetBytes(s, 0, s.Length, b, o));
        }

        public bool TryRead(out string str)
        {
            str = null;
            uint size;

            if (!TryRead7BitEncodedInt(out size)) return false;
            return TryReadValue(out str, (int)size, Encoding.UTF8.GetString);
        }

        public void Write(byte val) => WriteValue(val, (v, b, o) => b[o] = v);
        public void Write(sbyte val) => WriteValue(val, (v, b, o) => b[o] = (byte)v);
        public void Write(short v) => WriteValue(v, BitConverter.GetBytes);
        public void Write(ushort v) => WriteValue(v, BitConverter.GetBytes);
        public void Write(int v) => WriteValue(v, BitConverter.GetBytes);
        public void Write(uint v) => WriteValue(v, BitConverter.GetBytes);
        public void Write(long v) => WriteValue(v, BitConverter.GetBytes);
        public void Write(ulong v) => WriteValue(v, BitConverter.GetBytes);
        public void Write(float v) => WriteValue(v, BitConverter.GetBytes);
        public void Write(double v) => WriteValue(v, BitConverter.GetBytes);
        public bool TryRead(out byte v) => TryReadValue(out v, (b, o) => b[o]);
        public bool TryRead(out sbyte v) => TryReadValue(out v, (b, o) => (sbyte)b[o]);
        public bool TryRead(out short v) => TryReadValue(out v, BitConverter.ToInt16);
        public bool TryRead(out ushort v) => TryReadValue(out v, BitConverter.ToUInt16);
        public bool TryRead(out int v) => TryReadValue(out v, BitConverter.ToInt32);
        public bool TryRead(out uint v) => TryReadValue(out v, BitConverter.ToUInt32);
        public bool TryRead(out long v) => TryReadValue(out v, BitConverter.ToInt64);
        public bool TryRead(out ulong v) => TryReadValue(out v, BitConverter.ToUInt64);
        public bool TryRead(out float v) => TryReadValue(out v, BitConverter.ToSingle);
        public bool TryRead(out double v) => TryReadValue(out v, BitConverter.ToDouble);
    }
}
