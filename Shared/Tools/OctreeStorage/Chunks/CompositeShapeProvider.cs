namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class CompositeShapeProvider : DataProvider
    {
        public override int ProviderType => 10002;

        public override int Version => 2;

        public uint ProviderVersion { get; set; } = 3;

        public int Generator { get; set; } = 3;

        public int Seed { get; set; }

        public float Size { get; set; }

        public uint UnusedCompat { get; set; }

        public int GeneratorSeed { get; set; }

        public override int GetSize()
        {
            return base.GetSize() + SizeOf(ProviderVersion) + SizeOf(Generator) + SizeOf(Seed) + SizeOf(Size) + (ProviderVersion >= 2 ? SizeOf(UnusedCompat) : 0) + (ProviderVersion >= 3 ? SizeOf(GeneratorSeed) : 0);
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
            buffer.Write(ProviderVersion);
            buffer.Write(Generator);
            buffer.Write(Seed);
            buffer.Write(Size);

            if (ProviderVersion >= 2)
            {
                buffer.Write(UnusedCompat);
            }

            if (ProviderVersion == 3)
            {
                buffer.Write(GeneratorSeed);
            }
        }

        public static bool TryRead(ByteArrayBuffer reader, out CompositeShapeProvider data)
        {
            data = null;

            uint providerVersion;
            int generator;
            int seed;
            float size;
            uint unusedCompat = 0;
            int generatorSeed = 0;
            if (!reader.TryRead(out providerVersion) || providerVersion < 1) return false;
            if (!reader.TryRead(out generator)) return false;
            if (!reader.TryRead(out seed)) return false;
            if (!reader.TryRead(out size)) return false;
            if ((providerVersion >= 2 && !reader.TryRead(out unusedCompat)) || unusedCompat == 1) return false;
            if (providerVersion >= 3 && !reader.TryRead(out generatorSeed)) return false;

            data = new CompositeShapeProvider
            {
                ProviderVersion = providerVersion,
                Generator = generator,
                Seed = seed,
                Size = size,
                UnusedCompat = providerVersion >= 2 ? unusedCompat : 0,
                GeneratorSeed = providerVersion >= 3 ? generatorSeed : 0
            };

            return true;
        }
    }
}
