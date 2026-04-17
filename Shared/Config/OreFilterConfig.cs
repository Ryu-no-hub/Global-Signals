using System.Collections.Generic;
using System.Xml.Serialization;

namespace GlobalSignals.Shared.Config
{
    [XmlRoot("OreFilterConfig")]
    public class OreFilterConfig
    {
        public bool Enabled { get; set; }
        public bool VerboseLogging { get; set; }

        [XmlArrayItem("Ore")]
        public List<string> BlacklistedOres { get; set; }

        [XmlArrayItem("Ore")]
        public List<string> StoneFallbacks { get; set; }

        public OreFilterConfig()
        {
            Enabled = true;
            VerboseLogging = true;

            BlacklistedOres = new List<string>
            {
                "Gold_01",
                "Platinum_01",
                "Copper_01",
                "Galena_01"
            };

            StoneFallbacks = new List<string>
            {
                "Stone01",
                "Stone02",
                "Stone03",
                "Stone04",
                "Stone05"
            };
        }
    }
}