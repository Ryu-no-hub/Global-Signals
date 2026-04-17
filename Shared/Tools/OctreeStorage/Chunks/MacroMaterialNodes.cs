using System;
using System.Linq;

namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class MacroMaterialNodes : OctreeStorageChunk
    {
        public override OctreeStorageChunkType Type => OctreeStorageChunkType.MacroMaterialNodes;

        public override int Version => 2;

        public OctreeNode<ulong>[] Nodes { get; set; } = Array.Empty<OctreeNode<ulong>>();

        public override int GetSize()
        {
            return Nodes.Sum(e => SizeOf(e.Key) + SizeOf(e.ChildMask) + SizeOf(e.Data));
        }

        public override void WriteTo(ByteArrayBuffer buffer)
        {
            base.WriteTo(buffer);

            foreach (var node in Nodes)
            {
                buffer.Write(node.Key);
                buffer.Write(node.ChildMask);
                buffer.Write(node.Data);
            }
        }

        public static bool TryRead(ByteArrayBuffer buffer, uint version, out MacroMaterialNodes data)
        {
            data = null;

            if (version == 1)
            {
                var nodeSize = SizeOf<uint>() + SizeOf<byte>() + SizeOf<ulong>();
                var count = buffer.Length / nodeSize;
                if (buffer.Length % nodeSize != 0) return false;
                var nodes = new OctreeNode<ulong>[count];

                for (int i = 0; i < count; i++)
                {
                    uint key32;
                    byte childMask;
                    ulong value;
                    if (!buffer.TryRead(out key32)) return false;
                    if (!buffer.TryRead(out childMask)) return false;
                    if (!buffer.TryRead(out value)) return false;

                    ulong key =
                        key32 & 0x3FF |
                        (ulong)(key32 >> 10 & 0xFF) << 20 |
                        (ulong)(key32 >> 18 & 0x3FF) << 40 |
                        (ulong)(key32 >> 28 & 0x0F) << 60;

                    nodes[i] = new OctreeNode<ulong>
                    {
                        Key = key,
                        ChildMask = childMask,
                        Data = value
                    };
                }

                data = new MacroMaterialNodes
                {
                    Nodes = nodes
                };

                return true;
            }
            else if (version == 2)
            {
                var nodeSize = SizeOf<ulong>() + SizeOf<byte>() + SizeOf<ulong>();
                var count = buffer.Length / nodeSize;
                if (buffer.Length % nodeSize != 0) return false;
                var nodes = new OctreeNode<ulong>[count];

                for (int i = 0; i < count; i++)
                {
                    ulong key;
                    byte childMask;
                    ulong value;
                    if (!buffer.TryRead(out key)) return false;
                    if (!buffer.TryRead(out childMask)) return false;
                    if (!buffer.TryRead(out value)) return false;

                    nodes[i] = new OctreeNode<ulong>
                    {
                        Key = key,
                        ChildMask = childMask,
                        Data = value
                    };
                }

                data = new MacroMaterialNodes
                {
                    Nodes = nodes
                };

                return true;
            }

            return false;
        }
    }
}
