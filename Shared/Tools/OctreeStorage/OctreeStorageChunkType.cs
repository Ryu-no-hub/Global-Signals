namespace SERingAsteroids.OctreeStorage
{
    public enum OctreeStorageChunkType : ushort
    {
        StorageMetaData = 1,
        MaterialIndexTable = 2,
        MacroContentNodes = 3,
        MacroMaterialNodes = 4,
        ContentLeafProvider = 5,
        ContentLeafOctree = 6,
        MaterialLeafProvider = 7,
        MaterialLeafOctree = 8,
        DataProvider = 9,
        EndOfFile = ushort.MaxValue
    }
}
