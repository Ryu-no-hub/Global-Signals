using System;
using System.Collections.Generic;
using static TorchPlugin.FactionScoreManager;

public class PlayerScoreInfo
{
    public long IdentityId { get; set; }
    public string PlayerName { get; set; }
    public ulong SteamId { get; set; }
    public int Score { get; set; }

    public double ActivityHours { get; set; } 
    public double ActivityHours2Weeks { get; set; }
    public List<ActivityRecord> RecentActivityRecords { get; set; } = new List<ActivityRecord>();
    public DateTime LastActivityUpdateUtc { get; set; }
    public DateTime JoinedUtc { get; set; }
}