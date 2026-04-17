namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class EOF : OctreeStorageChunk
    {
        public override OctreeStorageChunkType Type => OctreeStorageChunkType.EndOfFile;

        public EOF()
        {
        }

        public EOF(int version)
        {
            Version = version;
        }

        public override int Version { get; }

        public override int GetSize() => 0;

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
        }

        public static bool TryRead(ByteArrayBuffer _, uint version, out EOF data)
        {
            data = new EOF((int)version);

            return true;
        }
    }
}
