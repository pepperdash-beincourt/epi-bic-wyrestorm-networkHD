using Newtonsoft.Json;

namespace PepperDash.Essentials.Plugin.Config
{
    /// <summary>
    /// Configuration for <see cref="NhdSinkDisplay"/>: a display whose only control path is the
    /// NetworkHD decoder it is plugged into.
    /// </summary>
    public class NhdSinkDisplayProperties
    {
        /// <summary>Key of the NetworkHD decoder device the display is connected to.</summary>
        [JsonProperty("decoderKey")]
        public string DecoderKey { get; set; }

        /// <summary>
        /// RS-232 string that turns the display on, sent through the decoder with the serial settings
        /// in the decoder's own <c>rs232</c> block. When this and <see cref="PowerOffCommand"/> are both
        /// empty, the decoder's sink-power proxy is used instead, which sends whatever power commands
        /// (CEC or RS-232) are stored on the endpoint.
        /// </summary>
        [JsonProperty("powerOnCommand")]
        public string PowerOnCommand { get; set; }

        /// <summary>RS-232 string that turns the display off. See <see cref="PowerOnCommand"/>.</summary>
        [JsonProperty("powerOffCommand")]
        public string PowerOffCommand { get; set; }

        /// <summary>How long the display is reported as warming up after power on. Default 10000.</summary>
        [JsonProperty("warmingTimeMs")]
        public uint WarmingTimeMs { get; set; } = 10000;

        /// <summary>How long the display is reported as cooling down after power off. Default 10000.</summary>
        [JsonProperty("coolingTimeMs")]
        public uint CoolingTimeMs { get; set; } = 10000;
    }
}
