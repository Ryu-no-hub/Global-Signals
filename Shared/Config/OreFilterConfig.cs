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
                "Anorthite_01",
                "Hematite_01",
                "Magnetite_01",
                "Electrum_01",
                "Troilite_01",
                "Kamacite_01",
                "Taenite_01",
                "Pentlandite_01",
                "Cobaltite_01",
                "Sperrylite_01",
                "Brannerite_01",
                "Petzite_01",
                "Galena_01",
                "Carnotite_01",
                "Fayalite_01",
                "Gold_02",
                "Platinum_02",
                "Wolframite_01",
                "Copper_01",
                "Titanium_01",
                "Aluminium_01",
                "Lithium_01",
                "Sulfur_01",
                "Nitre_01",
                "Galena_02"
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