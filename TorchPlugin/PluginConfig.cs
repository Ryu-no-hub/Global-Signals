using System;
using Shared.Config;
using Torch;
using Torch.Views;

namespace TorchPlugin
{
    [Serializable]
    public class PluginConfig : ViewModel, IPluginConfig
    {
        private bool enabled = true;
        private bool factionScore = true;
        private bool detectCodeChanges = true;
        // TODO: Implement your config fields and add the default values for Torch here.
        //       Be more conservative with changes and introduce new features as disabled
        //       at first, so admins can enable them first on their test deployments.
        //       Once the feature is stable set the default here to true to enable for
        //       newly created Torch deployments.
        private string gpsDescriptionString = "Global Radar Signal";
        private bool useConnectedGrids = false;


        //public bool UseConnectedGrids { get => _useConnectedGrids; set => SetValue(ref _useConnectedGrids, value); }

        [Display(Order = 1, GroupName = "General", Name = "Enable plugin", Description = "Enable the plugin")]
        public bool GlobalSignals
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        [Display(Order = 2, GroupName = "General", Name = "Faction score", Description = "Enable Faction Score")]
        public bool FactionScore
        {
            get => factionScore;
            set => SetValue(ref factionScore, value);
        }

        [Display(Order = 3, GroupName = "General", Name = "Detect code changes", Description = "Disable the plugin if any changes to the game code are detected before patching")]
        public bool DetectCodeChanges
        {
            get => detectCodeChanges;
            set => SetValue(ref detectCodeChanges, value);
        }

        // TODO: Encapsulate them as properties and define their Display properties
        [Display(Order = 4, GroupName = "General", Name = "Use Connected Grids", Description = "Don't know what that means")]
        public bool UseConnectedGrids
        {
            get => useConnectedGrids;
            set => SetValue(ref useConnectedGrids, value);
        }

        [Display(Order = 5, GroupName = "General", Name = "Gps Description String", Description = "GPS Description")]
        public string GpsDescriptionString
        {
            get => gpsDescriptionString;
            set => SetValue(ref gpsDescriptionString, value);
        }
    }
}