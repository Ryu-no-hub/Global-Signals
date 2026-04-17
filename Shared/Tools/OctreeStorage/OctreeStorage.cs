using System;
using System.Collections.Generic;
using System.Linq;
using SERingAsteroids.OctreeStorage.Chunks;

namespace SERingAsteroids.OctreeStorage
{
    public class OctreeStorage
    {
        public int Version => 2;

        public ushort AccessGridLod { get; set; } = 10;

        public MetaData MetaData { get; set; }

        public MaterialIndexTable MaterialIndexTable { get; set; }

        public DataProvider DataProvider { get; set; }

        public MacroContentNodes MacroContentNodes { get; set; } = new MacroContentNodes();

        public MacroMaterialNodes MacroMaterialNodes { get; set; } = new MacroMaterialNodes();

        public ContentLeaf[] ContentLeaves { get; set; } = Array.Empty<ContentLeaf>();

        public MaterialLeaf[] MaterialLeaves { get; set; } = Array.Empty<MaterialLeaf>();

        private int GetWrittenSize(OctreeStorageChunk chunk)
        {
            var size = chunk.GetSize();
            var writtenSize =
                ByteArrayBuffer.SizeOf7BitEncodedInt((uint)chunk.Type)
              + ByteArrayBuffer.SizeOf7BitEncodedInt((uint)chunk.Version)
              + ByteArrayBuffer.SizeOf7BitEncodedInt((uint)size);

            if (Version == 2 && chunk is MacroContentNodes && chunk.Version == 2)
            {
                var nodes = (MacroContentNodes)chunk;

                foreach (var node in nodes.Nodes)
                {
                    if ((node.Key >> 60) + 5 == AccessGridLod)
                    {
                        writtenSize += ByteArrayBuffer.SizeOf(node.Access);
                    }
                }
            }

            return writtenSize;
        }


        public int GetWrittenSize()
        {
            return ByteArrayBuffer.SizeOf("Octree")
                 + ByteArrayBuffer.SizeOf7BitEncodedInt((uint)Version)
                 + ByteArrayBuffer.SizeOf(AccessGridLod)
                 + GetWrittenSize(MetaData)
                 + GetWrittenSize(MaterialIndexTable)
                 + GetWrittenSize(DataProvider)
                 + GetWrittenSize(MacroContentNodes)
                 + GetWrittenSize(MacroMaterialNodes)
                 + ContentLeaves.Sum(e => GetWrittenSize(e))
                 + MaterialLeaves.Sum(e => GetWrittenSize(e))
                 + GetWrittenSize(new EOF());
        }

        public void WriteTo(ByteArrayBuffer buffer)
        {
            buffer.Write("Octree");
            buffer.Write7BitEncodedInt((uint)Version);
            buffer.Write(AccessGridLod);
            MetaData.WriteTo(buffer);
            MaterialIndexTable.WriteTo(buffer);
            DataProvider.WriteTo(buffer);
            MacroContentNodes.WriteTo(buffer, (uint)Version, AccessGridLod);
            MacroMaterialNodes.WriteTo(buffer);

            foreach (var leaf in ContentLeaves)
            {
                leaf.WriteTo(buffer);
            }

            foreach (var leaf in MaterialLeaves)
            {
                leaf.WriteTo(buffer);
            }

            var eof = new EOF();
            eof.WriteTo(buffer);
        }

        public byte[] GetBytes()
        {
            var buffer = new ByteArrayBuffer(GetWrittenSize());
            WriteTo(buffer);
            return buffer.ToArray();
        }

        private static bool TryRead(ByteArrayBuffer buffer, OctreeStorageChunkType type, uint origsize, uint version, uint fileVersion, ushort accessGridLod, out OctreeStorageChunk chunk)
        {
            bool ret;

            if (type == OctreeStorageChunkType.StorageMetaData && version == 1)
            {
                MetaData metadata;
                ret = MetaData.TryRead(buffer, out metadata);
                chunk = metadata;
            }
            else if (type == OctreeStorageChunkType.MaterialIndexTable && version == 1)
            {
                MaterialIndexTable matindex;
                ret = MaterialIndexTable.TryRead(buffer, out matindex);
                chunk = matindex;
            }
            else if (type == OctreeStorageChunkType.DataProvider && version == 2)
            {
                DataProvider provider;
                ret = DataProvider.TryRead(buffer, out provider);
                chunk = provider;
            }
            else if (type == OctreeStorageChunkType.MacroContentNodes && (version == 1 || version == 2))
            {
                MacroContentNodes nodes;
                ret = MacroContentNodes.TryRead(buffer, origsize, fileVersion, accessGridLod, version, out nodes);
                chunk = nodes;
            }
            else if (type == OctreeStorageChunkType.MacroMaterialNodes && (version == 1 || version == 2))
            {
                MacroMaterialNodes nodes;
                ret = MacroMaterialNodes.TryRead(buffer, version, out nodes);
                chunk = nodes;
            }
            else if (type == OctreeStorageChunkType.ContentLeafOctree && (version == 2 || version == 3))
            {
                ContentLeafOctree leaf;
                ret = ContentLeafOctree.TryRead(buffer, version, out leaf);
                chunk = leaf;
            }
            else if (type == OctreeStorageChunkType.ContentLeafProvider && (version == 2 || version == 3))
            {
                ContentLeafProvider leaf;
                ret = ContentLeafProvider.TryRead(buffer, version, out leaf);
                chunk = leaf;
            }
            else if (type == OctreeStorageChunkType.MaterialLeafOctree && (version == 2 || version == 3))
            {
                MaterialLeafOctree leaf;
                ret = MaterialLeafOctree.TryRead(buffer, version, out leaf);
                chunk = leaf;
            }
            else if (type == OctreeStorageChunkType.MaterialLeafProvider && (version == 2 || version == 3))
            {
                MaterialLeafProvider leaf;
                ret = MaterialLeafProvider.TryRead(buffer, version, out leaf);
                chunk = leaf;
            }
            else if (type == OctreeStorageChunkType.EndOfFile)
            {
                EOF eof;
                ret = EOF.TryRead(buffer, version, out eof);
                chunk = eof;
            }
            else
            {
                chunk = null;
                ret = false;
            }

            return ret;
        }

        private static bool TryRead(ByteArrayBuffer buffer, uint fileVersion, ushort accessGridLod, out OctreeStorageChunk chunk)
        {
            chunk = null;
            uint typeval;
            uint version;
            uint size;

            if (!buffer.TryRead7BitEncodedInt(out typeval))
                return false;

            if (!buffer.TryRead7BitEncodedInt(out version))
                return false;

            if (!buffer.TryRead7BitEncodedInt(out size))
                return false;

            var origsize = size;

            var type = (OctreeStorageChunkType)typeval;

            ByteArrayBuffer chunkbuffer;
            if (!buffer.TryRead((int)size, out chunkbuffer))
                return false;

            // File version 2 macro content nodes 2 spews out extra data at a certain LOD
            if (type == OctreeStorageChunkType.MacroContentNodes && fileVersion == 2 && version == 2)
            {
                for (int i = 0; i < size; i += 17)
                {
                    if (chunkbuffer[i + 7] >> 4 == accessGridLod - 5)
                    {
                        if (!buffer.TryRead((int)size, chunkbuffer)) return false;
                        size += 2;
                        i += 2;
                    }
                }
            }

            return TryRead(chunkbuffer, type, origsize, version, fileVersion, accessGridLod, out chunk);
        }

        public static bool TryRead(ByteArrayBuffer buffer, out OctreeStorage storage, Action<string> logAction = null)
        {
            storage = null;

            string filetype;
            byte version;

            if (!buffer.TryRead(out filetype) || filetype != "Octree")
            {
                logAction?.Invoke($"Not an Octree file at {buffer.Position}");
                return false;
            }

            if (!buffer.TryRead(out version) || version < 1 || version > 2)
            {
                logAction?.Invoke($"Unsupported version {version} at {buffer.Position}");
                return false;
            }

            ushort accessGridLod = 10;

            if (version == 2 && !buffer.TryRead(out accessGridLod))
            {
                logAction?.Invoke($"Cannot read AccessGridLOD at {buffer.Position}");
                return false;
            }

            MetaData metaData = null;
            MaterialIndexTable materialIndexTable = null;
            DataProvider provider = null;
            MacroContentNodes macroContentNodes = null;
            MacroMaterialNodes macroMaterialNodes = null;
            var contentLeaves = new List<ContentLeaf>();
            var materialLeaves = new List<MaterialLeaf>();
            EOF eof = null;
            OctreeStorageChunk chunk;

            while (TryRead(buffer, version, accessGridLod, out chunk))
            {
                logAction?.Invoke($"{chunk?.Type}");

                if (chunk is MetaData) metaData = (MetaData)chunk;
                else if (chunk is MaterialIndexTable) materialIndexTable = (MaterialIndexTable)chunk;
                else if (chunk is DataProvider) provider = (DataProvider)chunk;
                else if (chunk is MacroContentNodes) macroContentNodes = (MacroContentNodes)chunk;
                else if (chunk is MacroMaterialNodes) macroMaterialNodes = (MacroMaterialNodes)chunk;
                else if (chunk is ContentLeaf) contentLeaves.Add((ContentLeaf)chunk);
                else if (chunk is MaterialLeaf) materialLeaves.Add((MaterialLeaf)chunk);
                else if (chunk is EOF)
                {
                    eof = (EOF)chunk;
                    break;
                }
                else if (chunk == null)
                {
                    logAction?.Invoke($"Chunk is null at {buffer.Position}");
                    return false;
                }
            }

            if (metaData == null)
            {
                logAction?.Invoke("No metadata");
                return false;
            }

            if (materialIndexTable == null)
            {
                logAction?.Invoke("No material index table");
                return false;
            }

            if (provider == null)
            {
                logAction?.Invoke("No provider");
                return false;
            }

            if (macroContentNodes == null)
            {
                logAction?.Invoke("No macro content nodes");
                return false;
            }

            if (macroMaterialNodes == null)
            {
                logAction?.Invoke("No macro material nodes");
                return false;
            }

            if (contentLeaves.Count == 0)
            {
                logAction?.Invoke("No content leaves");
                return false;
            }

            if (materialLeaves.Count == 0)
            {
                logAction?.Invoke("No material leaves");
                return false;
            }

            if (eof == null)
            {
                logAction?.Invoke("No eof");
                return false;
            }

            storage = new OctreeStorage
            {
                AccessGridLod = accessGridLod,
                MetaData = metaData,
                MaterialIndexTable = materialIndexTable,
                DataProvider = provider,
                MacroContentNodes = macroContentNodes,
                MacroMaterialNodes = macroMaterialNodes,
                ContentLeaves = contentLeaves.ToArray(),
                MaterialLeaves = materialLeaves.ToArray()
            };

            return true;
        }

        public static OctreeStorage ReadFrom(byte[] data, Func<byte[], byte[]> decompress = null, Action<string> logAction = null)
        {
            var buffer = new ByteArrayBuffer(data);
            string filetype;
            OctreeStorage storage;

            if (data[0] != "Octree".Length || !buffer.TryRead(out filetype) || filetype != "Octree")
            {
                logAction?.Invoke($"data[0]={data[0]}");

                byte[] uncompressed;

                try
                {
                    uncompressed = decompress(data);
                }
                catch (Exception ex)
                {
                    return null;
                }

                buffer = new ByteArrayBuffer(uncompressed);
            }

            return TryRead(buffer, out storage, logAction) ? storage : null;
        }

        private static readonly string[] DefaultMaterials = new string[]
        {
            "Stone_01",
            "Stone_02",
            "Stone_03",
            "Stone_04",
            "Stone_05",
            "Iron_01",
            "Iron_02",
            "Nickel_01",
            "Cobalt_01",
            "Magnesium_01",
            "Silicon_01",
            "Silver_01",
            "Gold_01",
            "Platinum_01",
            "Uraninite_01",
            "Ice_01",
            "Ice_02",
            "Ice_03",
            "Carbon_01",
            "Potassium_01",
            "Phosphorus_01"
        };

        public static OctreeStorage CreateAsteroid(int seed, float size, int generatorSeed, int defaultMaterial = 0, IEnumerable<MaterialIndexEntry> materials = null, int? generator = null)
        {
            var defaultMaterials = DefaultMaterials.Select((e, i) => new MaterialIndexEntry { Index = (uint)i, Name = e }).ToArray();

            int isize = 32;
            int maxlod = 1;

            while (isize < size)
            {
                isize *= 2;
                maxlod += 1;
            }

            if (maxlod > 16)
            {
                throw new ArgumentException("Size too large", nameof(size));
            }

            ulong key0 = (ulong)(maxlod & 0x0F) << 60;

            return new OctreeStorage
            {
                MetaData = new MetaData
                {
                    DefaultMaterial = (byte)defaultMaterial,
                    SizeX = isize,
                    SizeY = isize,
                    SizeZ = isize
                },
                MaterialIndexTable = new MaterialIndexTable
                {
                    Materials = materials?.ToArray() ?? defaultMaterials,
                },
                DataProvider = new CompositeShapeProvider
                {
                    ProviderVersion = 3,
                    Generator = generator ?? 4,
                    Seed = seed,
                    Size = size,
                    UnusedCompat = 0,
                    GeneratorSeed = generatorSeed
                },
                MacroContentNodes = new MacroContentNodes(),
                MacroMaterialNodes = new MacroMaterialNodes(),
                ContentLeaves = new ContentLeaf[]
                {
                    new ContentLeafProvider
                    {
                        Key = key0
                    }
                },
                MaterialLeaves = new MaterialLeaf[]
                {
                    new MaterialLeafProvider
                    {
                        Key = key0
                    }
                }
            };
        }

        public static OctreeStorage CreatePlanet(long seed, long radius, string generatorName, int defaultMaterial = 0, IEnumerable<MaterialIndexEntry> materials = null)
        {
            var defaultMaterials = DefaultMaterials.Select((e, i) => new MaterialIndexEntry { Index = (uint)i, Name = e }).ToArray();

            int isize = 32;
            int maxlod = 1;

            while (isize < radius * 2)
            {
                isize *= 2;
                maxlod += 1;
            }

            if (maxlod > 16)
            {
                throw new ArgumentException("Radius too large", nameof(radius));
            }

            ulong key0 = (ulong)(maxlod & 0x0F) << 60;

            return new OctreeStorage
            {
                MetaData = new MetaData
                {
                    DefaultMaterial = (byte)defaultMaterial,
                    SizeX = isize,
                    SizeY = isize,
                    SizeZ = isize
                },
                MaterialIndexTable = new MaterialIndexTable
                {
                    Materials = materials?.ToArray() ?? defaultMaterials,
                },
                DataProvider = new PlanetStorageProvider
                {
                    ProviderVersion = 1,
                    Seed = seed,
                    Radius = radius,
                    GeneratorName = generatorName
                },
                MacroContentNodes = new MacroContentNodes(),
                MacroMaterialNodes = new MacroMaterialNodes(),
                ContentLeaves = new ContentLeaf[] {
                    new ContentLeafProvider
                    {
                        Key = key0
                    }
                },
                MaterialLeaves = new MaterialLeaf[] {
                    new MaterialLeafProvider
                    {
                        Key = key0
                    }
                }
            };
        }
    }
}
