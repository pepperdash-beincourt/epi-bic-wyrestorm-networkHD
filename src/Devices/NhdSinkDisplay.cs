using System;
using System.Timers;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Devices.Common.Displays;
using PepperDash.Essentials.Plugin.Config;

namespace PepperDash.Essentials.Plugin
{
    /// <summary>
    /// A display that is controlled through the NetworkHD decoder it is plugged into, for a display
    /// with no network connection of its own. Power commands go out as RS-232 strings through the
    /// decoder, or through the decoder's sink-power proxy when no strings are configured.
    /// </summary>
    /// <remarks>
    /// The path is one-way: the controller API sends to the display but returns nothing from it, so
    /// the power state reported here is the last command sent, not something the display confirmed. A
    /// display switched by its own remote or front panel will be out of step until the next command.
    /// For that reason both commands are always sent when asked for, whatever state is assumed.
    /// </remarks>
    public class NhdSinkDisplay : TwoWayDisplayBase
    {
        private readonly NhdSinkDisplayProperties _props;

        private bool _powerIsOn;
        private bool _isWarmingUp;
        private bool _isCoolingDown;

        public NhdSinkDisplay(string key, string name, NhdSinkDisplayProperties props)
            : base(key, name)
        {
            _props = props ?? new NhdSinkDisplayProperties();

            WarmupTime = _props.WarmingTimeMs;
            CooldownTime = _props.CoolingTimeMs;

            InputPorts.Add(new RoutingInputPort(RoutingPortNames.HdmiIn1, eRoutingSignalType.AudioVideo,
                eRoutingPortConnectionType.Hdmi, RoutingPortNames.HdmiIn1, this));
        }

        protected override Func<bool> PowerIsOnFeedbackFunc => () => _powerIsOn;

        protected override Func<bool> IsWarmingUpFeedbackFunc => () => _isWarmingUp;

        protected override Func<bool> IsCoolingDownFeedbackFunc => () => _isCoolingDown;

        // One input: the decoder.
        protected override Func<string> CurrentInputFeedbackFunc => () => RoutingPortNames.HdmiIn1;

        public override void PowerOn()
        {
            if (!SendPower(true))
                return;

            CooldownTimer?.Stop();
            _isCoolingDown = false;
            _powerIsOn = true;
            _isWarmingUp = WarmupTime > 0;
            FireAll();

            if (!_isWarmingUp)
                return;

            WarmupTimer?.Stop();
            WarmupTimer = new Timer(WarmupTime) { AutoReset = false };
            WarmupTimer.Elapsed += (s, e) =>
            {
                _isWarmingUp = false;
                IsWarmingUpFeedback.InvokeFireUpdate();
            };
            WarmupTimer.Start();
        }

        public override void PowerOff()
        {
            if (!SendPower(false))
                return;

            WarmupTimer?.Stop();
            _isWarmingUp = false;
            _powerIsOn = false;
            _isCoolingDown = CooldownTime > 0;
            FireAll();

            if (!_isCoolingDown)
                return;

            CooldownTimer?.Stop();
            CooldownTimer = new Timer(CooldownTime) { AutoReset = false };
            CooldownTimer.Elapsed += (s, e) =>
            {
                _isCoolingDown = false;
                IsCoolingDownFeedback.InvokeFireUpdate();
            };
            CooldownTimer.Start();
        }

        public override void PowerToggle()
        {
            if (_powerIsOn)
                PowerOff();
            else
                PowerOn();
        }

        /// <summary>The display has a single input (the decoder), so there is nothing to switch.</summary>
        public override void ExecuteSwitch(object selector)
        {
        }

        // Sends the power command through the decoder. False when it could not be sent at all (decoder
        // missing, or it rejected the command), in which case the assumed state is left alone.
        private bool SendPower(bool on)
        {
            var decoder = DeviceManager.GetDeviceForKey(_props.DecoderKey) as NhdBaseDevice;
            if (decoder == null)
            {
                this.LogError("Decoder '{DecoderKey}' not found or not a NetworkHD endpoint - power {State} not sent",
                    _props.DecoderKey, on ? "on" : "off");
                return false;
            }

            var command = on ? _props.PowerOnCommand : _props.PowerOffCommand;

            try
            {
                if (string.IsNullOrWhiteSpace(_props.PowerOnCommand) && string.IsNullOrWhiteSpace(_props.PowerOffCommand))
                    decoder.SendPowerProxyCommand(on ? "on" : "off");
                else if (string.IsNullOrWhiteSpace(command))
                {
                    this.LogError("No power {State} command configured - nothing sent", on ? "on" : "off");
                    return false;
                }
                else
                    decoder.Send232Command(command);

                this.LogInformation("Power {State} sent through decoder '{DecoderKey}'", on ? "on" : "off", decoder.Key);
                return true;
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Power {State} could not be sent through decoder '{DecoderKey}': {Message}",
                    on ? "on" : "off", decoder.Key, ex.Message);
                return false;
            }
        }

        private void FireAll()
        {
            PowerIsOnFeedback.InvokeFireUpdate();
            IsWarmingUpFeedback.InvokeFireUpdate();
            IsCoolingDownFeedback.InvokeFireUpdate();
        }
    }
}
