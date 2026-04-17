namespace SERingAsteroids.OctreeStorage.Chunks
{
    public class OctreeNode<TKey>
        where TKey : struct
    {
        public TKey Key { get; set; }
        public byte ChildMask { get; set; }
        public ulong Data { get; set; }
        public ushort Access { get; set; }
    }
}
