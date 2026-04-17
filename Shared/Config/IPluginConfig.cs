using System.ComponentModel;

namespace Shared.Config
{
    public interface IPluginConfig : INotifyPropertyChanged
    {
        // Enables the signals
        bool GlobalSignals { get; set; }

        // Enables Faction Score
        bool FactionScore { get; set; }

        // Enables checking for changes in patched game code (disable this on Proton/Linux)
        bool DetectCodeChanges { get; set; }

        // TODO: Add config properties here, then extend the implementing classes accordingly
        string GpsDescriptionString { get; set; }

        bool UseConnectedGrids { get; set; }
    }
}