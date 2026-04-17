using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using GlobalSignals.Shared.Config;
using Shared.Logging;


namespace GlobalSignals.Shared.Runtime
{
    public static class OreFilterState
    {
        private static readonly object Sync = new object();
        private static IPluginLogger _log;

        public static string ConfigPath { get; private set; }
        public static OreFilterConfig Config { get; private set; }

        public static void Initialize(IPluginLogger log, string configPath)
        {
            _log = log;
            ConfigPath = configPath;
        }

        public static void Load()
        {
            lock (Sync)
            {
                try
                {
                    string dir = Path.GetDirectoryName(ConfigPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (!File.Exists(ConfigPath))
                    {
                        Config = new OreFilterConfig();
                        Save();
                        if (_log != null)
                            _log.Info("[OreFilter] Created default config: " + ConfigPath);
                        return;
                    }

                    XmlSerializer serializer = new XmlSerializer(typeof(OreFilterConfig));
                    using (FileStream stream = File.OpenRead(ConfigPath))
                    {
                        Config = (OreFilterConfig)serializer.Deserialize(stream);
                    }

                    if (Config == null)
                        Config = new OreFilterConfig();

                    if (Config.BlacklistedOres == null)
                        Config.BlacklistedOres = new List<string>();

                    if (Config.StoneFallbacks == null)
                        Config.StoneFallbacks = new List<string>();

                    Config.BlacklistedOres = Config.BlacklistedOres
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    Config.StoneFallbacks = Config.StoneFallbacks
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (_log != null)
                        _log.Info("[OreFilter] Config loaded.");
                }
                catch (Exception ex)
                {
                    if (_log != null)
                        _log.Error(ex, "[OreFilter] Failed to load config, using defaults.");

                    Config = new OreFilterConfig();
                }
            }
        }

        public static void Save()
        {
            lock (Sync)
            {
                XmlSerializer serializer = new XmlSerializer(typeof(OreFilterConfig));
                using (FileStream stream = File.Create(ConfigPath))
                {
                    serializer.Serialize(stream, Config ?? new OreFilterConfig());
                }
            }
        }

        public static bool Enabled
        {
            get { return Config != null && Config.Enabled; }
        }

        public static bool VerboseLogging
        {
            get { return Config != null && Config.VerboseLogging; }
        }

        public static bool IsBlacklisted(string subtypeName)
        {
            if (string.IsNullOrWhiteSpace(subtypeName))
                return false;

            if (Config == null || Config.BlacklistedOres == null)
                return false;

            return Config.BlacklistedOres.Contains(subtypeName, StringComparer.OrdinalIgnoreCase);
        }

        public static IPluginLogger Log
        {
            get { return _log; }
        }
    }
}