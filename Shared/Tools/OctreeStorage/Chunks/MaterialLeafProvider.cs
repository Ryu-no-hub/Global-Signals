namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class MaterialLeafProvider : MaterialLeaf
    {
        public override OctreeStorageChunkType Type => OctreeStorageChunkType.MaterialLeafProvider;

        public override int Version => 3;

        public override int GetSize()
        {
            return SizeOf(Key);
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
            buffer.Write(Key);
        }

        public static bool TryRead(ByteArrayBuffer buffer, uint version, out MaterialLeafProvider data)
        {
            data = null;

            ulong key;

            if (version == 2)
            {
                uint key32;
                if (!buffer.TryRead(out key32)) return false;

                key =
                    key32 & 0x3FF |
                    (ulong)(key32 >> 10 & 0xFF) << 20 |
                    (ulong)(key32 >> 18 & 0x3FF) << 40 |
                    (ulong)(key32 >> 28 & 0x0F) << 60;
            }
            else if (version == 3)
            {
                if (!buffer.TryRead(out key)) return false;
            }
            else
            {
                return false;
            }

            data = new MaterialLeafProvider
            {
                Key = key
            };

            return true;
        }
    }
}
