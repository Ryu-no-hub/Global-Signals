namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class PlanetStorageProvider : DataProvider
    {
        public override int ProviderType => 10042;

        public override int Version => 2;

        public ulong ProviderVersion { get; set; } = 1;

        public long Seed { get; set; }

        public double Radius { get; set; }

        public string GeneratorName { get; set; }

        public override int GetSize()
        {
            return base.GetSize() + SizeOf(ProviderVersion) + SizeOf(Seed) + SizeOf(Radius) + SizeOf(GeneratorName);
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
            buffer.Write(ProviderVersion);
            buffer.Write(Seed);
            buffer.Write(Radius);
            buffer.Write(GeneratorName);
        }

        public static bool TryRead(ByteArrayBuffer reader, out PlanetStorageProvider provider)
        {
            provider = null;

            ulong providerVersion;
            long seed;
            double radius;
            string generatorName;
            if (!reader.TryRead(out providerVersion)) return false;
            if (!reader.TryRead(out seed)) return false;
            if (!reader.TryRead(out radius)) return false;
            if (!reader.TryRead(out generatorName)) return false;

            provider = new PlanetStorageProvider
            {
                ProviderVersion = providerVersion,
                Seed = seed,
                Radius = radius,
                GeneratorName = generatorName
            };

            return true;
        }
    }
}
