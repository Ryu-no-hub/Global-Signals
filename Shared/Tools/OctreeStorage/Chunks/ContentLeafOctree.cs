using System;
using System.Linq;

namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class ContentLeafOctree : ContentLeaf
    {
        public override OctreeStorageChunkType Type => OctreeStorageChunkType.ContentLeafOctree;

        public override int Version => 3;

        public int TreeHeight { get; set; }

        public byte DefaultContent { get; set; }

        public OctreeNode<uint>[] Nodes { get; set; } = Array.Empty<OctreeNode<uint>>();

        public override int GetSize()
        {
            return SizeOf(Key) + SizeOf(TreeHeight) + SizeOf(DefaultContent) + Nodes.Sum(e => SizeOf(e.Key) + SizeOf(e.ChildMask) + SizeOf(e.Data));
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);
            buffer.Write(Key);
            buffer.Write(TreeHeight);
            buffer.Write(DefaultContent);

            foreach (var node in Nodes)
            {
                buffer.Write(node.Key);
                buffer.Write(node.ChildMask);
                buffer.Write(node.Data);
            }
        }

        public static bool TryRead(ByteArrayBuffer buffer, uint version, out ContentLeafOctree data)
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

            int treeHeight;
            byte defaultContent;

            if (!buffer.TryRead(out treeHeight)) return false;
            if (!buffer.TryRead(out defaultContent)) return false;

            var itemSize = SizeOf<uint>() + SizeOf<byte>() + SizeOf<ulong>();
            if (buffer.Length % itemSize != 0) return false;
            var count = buffer.Length / itemSize;
            var nodes = new OctreeNode<uint>[count];

            for (int i = 0; i < count; i++)
            {
                uint nodeKey;
                byte childMask;
                ulong value;

                if (!buffer.TryRead(out nodeKey)) return false;
                if (!buffer.TryRead(out childMask)) return false;
                if (!buffer.TryRead(out value)) return false;

                nodes[i] = new OctreeNode<uint>
                {
                    Key = nodeKey,
                    ChildMask = childMask,
                    Data = value
                };
            }

            data = new ContentLeafOctree
            {
                Key = key,
                TreeHeight = treeHeight,
                DefaultContent = defaultContent,
                Nodes = nodes
            };

            return true;
        }
    }
}
