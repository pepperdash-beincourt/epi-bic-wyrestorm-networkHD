using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Plugin.Config;

namespace PepperDash.Essentials.Plugin
{
    public class NhdSinkDisplayFactory : EssentialsPluginDeviceFactory<NhdSinkDisplay>
    {
        public NhdSinkDisplayFactory()
        {
            MinimumEssentialsFrameworkVersion = "3.0.0";
            TypeNames = new List<string> { "nhd-sink-display", "nhdsinkdisplay" };
        }

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            var props = dc.Properties?.ToObject<NhdSinkDisplayProperties>();
            if (props == null || string.IsNullOrWhiteSpace(props.DecoderKey))
            {
                Debug.LogError("[{key}] Factory: {name} needs a decoderKey", dc.Key, dc.Name);
                return null;
            }

            return new NhdSinkDisplay(dc.Key, dc.Name, props);
        }
    }
}
