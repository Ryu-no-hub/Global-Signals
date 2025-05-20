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
        private bool detectCodeChanges = true;
        // TODO: Implement your config fields and add the default values for Torch here.
        //       Be more conservative with changes and introduce new features as disabled
        //       at first, so admins can enable them first on their test deployments.
        //       Once the feature is stable set the default here to true to enable for
        //       newly created Torch deployments.
        private string _gpsDescriptionString = "Global Radar Signal";
        private bool _useConnectedGrids = false;

        public string GpsDescriptionString { get => _gpsDescriptionString; set => SetValue(ref _gpsDescriptionString, value); }
        public bool UseConnectedGrids { get => _useConnectedGrids; set => SetValue(ref _useConnectedGrids, value); }

        [Display(Order = 1, GroupName = "General", Name = "Enable plugin", Description = "Enable the plugin")]
        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        [Display(Order = 2, GroupName = "General", Name = "Detect code changes", Description = "Disable the plugin if any changes to the game code are detected before patching")]
        public bool DetectCodeChanges
        {
            get => detectCodeChanges;
            set => SetValue(ref detectCodeChanges, value);
        }

        // TODO: Encapsulate them as properties and define their Display properties
    }
}