using System;

namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class MaterialIndexTable : OctreeStorageChunk
    {
        public override OctreeStorageChunkType Type => OctreeStorageChunkType.MaterialIndexTable;

        public override int Version => 1;

        public MaterialIndexEntry[] Materials { get; set; } = Array.Empty<MaterialIndexEntry>();

        public override int GetSize()
        {
            int size = SizeOf(Materials.Length);

            foreach (var entry in Materials)
            {
                size += SizeOf7BitEncodedInt(entry.Index) + SizeOf(entry.Name);
            }

            return size;
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
            buffer.Write(Materials.Length);

            foreach (var entry in Materials)
            {
                buffer.Write7BitEncodedInt(entry.Index);
                buffer.Write(entry.Name);
            }
        }

        public static bool TryRead(ByteArrayBuffer buffer, out MaterialIndexTable data)
        {
            data = null;

            int count;
            if (!buffer.TryRead(out count)) return false;

            var mats = new MaterialIndexEntry[count];

            for (int i = 0; i < mats.Length; i++)
            {
                uint index;
                string name;
                if (!buffer.TryRead7BitEncodedInt(out index)) return false;
                if (!buffer.TryRead(out name)) return false;
                mats[i] = new MaterialIndexEntry
                {
                    Index = index,
                    Name = name
                };
            }

            data = new MaterialIndexTable
            {
                Materials = mats
            };

            return true;
        }
    }
}
