namespace SERingAsteroids.OctreeStorage.Chunks
{
    public abstract class DataProvider : OctreeStorageChunk
    {
        public override OctreeStorageChunkType Type => OctreeStorageChunkType.DataProvider;

        public abstract int ProviderType { get; }

        public override int GetSize()
        {
            return SizeOf(ProviderType);
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
            buffer.Write(ProviderType);
        }

        public static bool TryRead(ByteArrayBuffer buffer, out DataProvider data)
        {
            data = null;
            int providerType;

            if (!buffer.TryRead(out providerType)) return false;

            switch (providerType)
            {
                case 10002:
                    {
                        CompositeShapeProvider provider;
                        bool ret = CompositeShapeProvider.TryRead(buffer, out provider);
                        data = provider;
                        return ret;
                    }
                case 10042:
                    {
                        PlanetStorageProvider provider;
                        bool ret = PlanetStorageProvider.TryRead(buffer, out provider);
                        data = provider;
                        return ret;
                    }
                default: return false;
            }
        }
    }
}
