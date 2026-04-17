using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json; // Torch already references this
using System.Linq;

public static class TorchPlayersCache
{
    public static IReadOnlyList<TorchPlayerInfo> Players { get; private set; }
    public static Dictionary<long, TorchPlayerInfo> ByIdentity { get; private set; }
    public static Dictionary<ulong, TorchPlayerInfo> BySteam { get; private set; }

    public static void Load(string instancePath)
    {
        var file = Path.Combine(instancePath, "players.json");
        if (!File.Exists(file))
        {
            Players = Array.Empty<TorchPlayerInfo>();
            ByIdentity = new Dictionary<long, TorchPlayerInfo>();
            BySteam = new Dictionary<ulong, TorchPlayerInfo>();
            return;
        }

        var json = File.ReadAllText(file);
        var list = JsonConvert.DeserializeObject<List<TorchPlayerInfo>>(json)
                   ?? new List<TorchPlayerInfo>();

        Players = list;
        ByIdentity = list.Where(p => p.IdentityID != 0).ToDictionary(p => p.IdentityID, p => p);
        BySteam = list.Where(p => p.SteamID != 0).ToDictionary(p => p.SteamID, p => p);
    }
}
