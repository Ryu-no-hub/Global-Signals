namespace SERingAsteroids.OctreeStorage
{
    public abstract class OctreeStorageChunk
    {
        public abstract OctreeStorageChunkType Type { get; }
        public abstract int Version { get; }

        public abstract int GetSize();

        protected static int SizeOf<T>(T _) where T : struct => ByteArrayBuffer.SizeOf<T>();

        protected static int SizeOf<T>() where T : struct => ByteArrayBuffer.SizeOf<T>();

        protected static int SizeOf(string str) => ByteArrayBuffer.SizeOf(str);

        protected static int SizeOf7BitEncodedInt(uint val) => ByteArrayBuffer.SizeOf7BitEncodedInt(val);

        public virtual void WriteTo(ByteArrayBuffer buffer)
        {
            buffer.Write7BitEncodedInt((uint)Type);
            buffer.Write7BitEncodedInt((uint)Version);
            buffer.Write7BitEncodedInt((uint)GetSize());
        }
    }
}
