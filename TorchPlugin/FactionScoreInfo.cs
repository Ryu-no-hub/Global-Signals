using System;
using System.Collections.Generic;

public class FactionScoreInfo
{
    public long FactionId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Name { get; set; }
    public string Tag { get; set; }
    public int Score { get; set; }
    public int Power { get; set; }
    public float TerritoryControl { get; set; }
    public int Activity { get; set; }
    public double Hours2W { get; set; }
    public List<string> CapturedPlanets { get; set; } = new List<string>();
    public List<FactionScoreHistoryEntry> ScoreHistory { get; set; } = new List<FactionScoreHistoryEntry>();
}

public class FactionScoreHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public int Score { get; set; }
}