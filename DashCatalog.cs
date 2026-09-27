using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace User.FXProRpmSync
{
    public class DashInfo
    {
        public string Id;
        public string Name;
        /// <summary>SimPro's preview picture of the dash, or null.</summary>
        public string ImagePath;
    }

    /// <summary>
    /// The FX Pro's built-in dashes, with SimPro's names and preview pictures. A dash id is the screen page number
    /// (0-37) that ends up in the preset's screens part.
    /// </summary>
    public static class DashCatalog
    {
        /// <summary>"BaseSettings": the wheel's force feedback settings screen, not a dash to drive with.</summary>
        public const string SettingsPageId = "7";

        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SIMAGIC", "Simpro3", "config", "dash", "offical_dash");

        private static List<DashInfo> all;

        public static IReadOnlyList<DashInfo> All => all ?? (all = Load());

        public static DashInfo Find(string id) =>
            id == null ? null : All.FirstOrDefault(d => d.Id == id) ?? new DashInfo { Id = id, Name = "Dash " + id };

        public static string NameOf(string id) => id == null ? "none" : Find(id).Name;

        private static List<DashInfo> Load()
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(Folder, "old_offical_dash_list.json")));
                return json["dash_list"]
                    .Select(d =>
                    {
                        var image = (string)d["images"]?.FirstOrDefault();
                        var path = image == null ? null : Path.Combine(Folder, image.Replace('/', Path.DirectorySeparatorChar));
                        return new DashInfo
                        {
                            Id = (string)d["uuid"],
                            Name = ((string)d["name"] ?? "").Trim(),
                            ImagePath = path != null && File.Exists(path) ? path : null,
                        };
                    })
                    .Where(d => int.TryParse(d.Id, out _))
                    .OrderBy(d => int.Parse(d.Id))
                    .ToList();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[FXProRpmSync] couldn't read SimPro's dash list: " + ex.Message);
                return Enumerable.Range(0, 38).Select(i => new DashInfo { Id = i.ToString(), Name = "Dash " + i }).ToList();
            }
        }
    }
}
