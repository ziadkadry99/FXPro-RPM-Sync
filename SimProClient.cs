using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace User.FXProRpmSync
{
    /// <summary>
    /// Talks to SimPro Manager 3's local RPC API (the same one its own UI uses).
    /// Every method is POST {base}/{method_name} with a JSON body; responses look like
    /// { "status": 200, "message": "", "result": ... }.
    /// </summary>
    public class SimProClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public string BaseUrl { get; set; } = "http://127.0.0.1:4010/simpro/api/v3";

        public class Wheel
        {
            public string DeviceUuid;
            public string ProductUuid;
            public string Name;

            /// <summary>SimPro "old device" (e.g. FX Pro): palette colors only, no per-gear RPM curves.</summary>
            public bool OldDevice;
        }

        public async Task<JToken> Call(string method, object body)
        {
            var json = JsonConvert.SerializeObject(body ?? new object());
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var resp = await Http.PostAsync(BaseUrl + "/" + method, content).ConfigureAwait(false))
            {
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var obj = JObject.Parse(text);
                var status = obj.Value<int?>("status") ?? (int)resp.StatusCode;
                if (status < 200 || status >= 300)
                    throw new Exception($"SimPro {method} failed: {status} {obj.Value<string>("message")}");
                return obj["result"];
            }
        }

        /// <summary>First connected device of type "wheel" (e.g. FX PRO), or null.</summary>
        public async Task<Wheel> FindWheel()
        {
            var list = await Call("get_device_list", new { }).ConfigureAwait(false) as JArray;
            var w = list?.FirstOrDefault(d => (string)d["product_type"] == "wheel");
            if (w == null) return null;
            return new Wheel
            {
                DeviceUuid = (string)w["device_uuid"],
                ProductUuid = (string)w["product_uuid"],
                Name = (string)w["product_name"],
                OldDevice = (bool?)w["old_device"] ?? true,
            };
        }

        /// <summary>
        /// The max RPM SimPro currently reads from the running game (the UI shows it as "Current Game Max RPM";
        /// the API calls it maxCarSpeed). The wheel scales its % thresholds against this value. 0 when unknown.
        /// </summary>
        public async Task<double> GetGameMaxRpm()
        {
            var games = await Call("game_get_running_list", new { }).ConfigureAwait(false) as JArray;
            return games?.Select(g => (double?)g["maxCarSpeed"] ?? 0).FirstOrDefault(v => v > 0) ?? 0;
        }

        /// <summary>Returns { preset_uuid, config:{ rpm_lights:{...}, ... } } for the active preset.</summary>
        public async Task<JObject> GetSelectedPreset(Wheel w)
        {
            return await Call("preset_get_selected_dev_config", new
            {
                device_uuid = w.DeviceUuid,
                product_uuid = w.ProductUuid,
            }).ConfigureAwait(false) as JObject;
        }

        /// <summary>Applies one part of a preset live (not saved to the preset until the user hits Save in SimPro).</summary>
        public Task SetPart(Wheel w, string presetUuid, string partType, int partId, JObject config)
        {
            return Call("preset_set_dev_config", new
            {
                device_uuid = w.DeviceUuid,
                product_uuid = w.ProductUuid,
                preset_uuid = presetUuid,
                part_type = partType,
                part_id = partId,
                config,
            });
        }
    }
}
