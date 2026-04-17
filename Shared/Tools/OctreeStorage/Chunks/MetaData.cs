namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class MetaData : OctreeStorageChunk
    {
        public override OctreeStorageChunkType Type => OctreeStorageChunkType.StorageMetaData;
        public override int Version => 1;

        public int LeafLodCount { get; set; } = 4;
        public int SizeX { get; set; }
        public int SizeY { get; set; }
        public int SizeZ { get; set; }
        public byte DefaultMaterial { get; set; }

        public override int GetSize()
        {
            return SizeOf(LeafLodCount) + SizeOf(SizeX) + SizeOf(SizeY) + SizeOf(SizeZ) + SizeOf(DefaultMaterial);
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
            buffer.Write(LeafLodCount);
            buffer.Write(SizeX);
            buffer.Write(SizeY);
            buffer.Write(SizeZ);
            buffer.Write(DefaultMaterial);
        }

        public static bool TryRead(ByteArrayBuffer reader, out MetaData data)
        {
            data = null;
            int leafLodCount;
            int sizeX;
            int sizeY;
            int sizeZ;
            byte defaultMaterial;

            if (!reader.TryRead(out leafLodCount)) return false;

            if (!reader.TryRead(out sizeX)) return false;

            if (!reader.TryRead(out sizeY)) return false;

            if (!reader.TryRead(out sizeZ)) return false;

            if (!reader.TryRead(out defaultMaterial)) return false;

            data = new MetaData
            {
                LeafLodCount = leafLodCount,
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = sizeZ,
                DefaultMaterial = defaultMaterial,
            };

            return true;
        }
    }
}
