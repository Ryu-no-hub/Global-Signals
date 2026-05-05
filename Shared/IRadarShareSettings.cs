namespace TerritoryBeaconShared
{
    public enum GlobalRadarShareMode
    {
        OnlyMe = 0,
        MyFaction = 1,
        Everyone = 2,
        Factions = 3,
        Players = 4
    }

    public interface IRadarShareSettings
    {
        GlobalRadarShareMode ShareMode { get; }
        System.Collections.Generic.HashSet<long> SharedFactionIds { get; }
        System.Collections.Generic.HashSet<long> SharedPlayerIds { get; }
    }
}