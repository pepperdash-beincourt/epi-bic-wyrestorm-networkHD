using System;
using System.Collections.Generic;
using System.Linq;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Routing;
using PepperDash.Essentials.Plugin.Comms;
using PepperDash.Essentials.Plugin.Mock;

namespace PepperDash.Essentials.Plugin.Routing;

public class NhdGlobalRouter : EssentialsDevice, IRoutingMidpointWithFeedback
{
    private static readonly NhdGlobalRouter _instance = new();
    private readonly NhdPrimaryStreamDomainRouter _primaryStreamRouter;
    private readonly NhdUsbDomainRouter _usbRouter;
    private readonly NhdControlDomainRouter _controlRouter;

    public const string InstanceKey = "NhdRouter";
    public const string RouteOff = "$off";
    public const string NoSourceText = "No Source";

    private NhdGlobalRouter()
        : base(InstanceKey)
    {
        _primaryStreamRouter = new NhdPrimaryStreamDomainRouter();
        _usbRouter = new NhdUsbDomainRouter();
        _controlRouter = new NhdControlDomainRouter();

        InputPorts = new RoutingPortCollection<RoutingInputPort>();
        OutputPorts = new RoutingPortCollection<RoutingOutputPort>();

        InputSlots = new Dictionary<string, INhdInputSlot>();
        OutputSlots = new Dictionary<string, NhdMatrixOutput>();

        AddPostActivationAction(BuildMatrixRouting);
        AddPostActivationAction(BuildTieLines);
    }

    public static NhdGlobalRouter Instance => _instance;

    public RoutingPortCollection<RoutingInputPort> InputPorts { get; private set; }
    public RoutingPortCollection<RoutingOutputPort> OutputPorts { get; private set; }

    public Dictionary<string, INhdInputSlot> InputSlots { get; private set; }
    public Dictionary<string, NhdMatrixOutput> OutputSlots { get; private set; }

    // IRoutingMidpointWithFeedback feedback surface (consumed by core routing / Mobile Control).
    public List<RouteSwitchDescriptor> CurrentRoutes { get; } = new List<RouteSwitchDescriptor>();
    public event RouteChangedEventHandler RouteChanged;

    public static string GetRouterInputPortKeyForEndpointPort(string endpointKey, string endpointPortKey)
    {
        if (string.IsNullOrWhiteSpace(endpointKey))
            return null;

        if (string.IsNullOrWhiteSpace(endpointPortKey)
            || endpointPortKey.Equals(NhdPortKeys.Stream, StringComparison.OrdinalIgnoreCase))
            return endpointKey;

        return string.Format("{0}-in-{1}", endpointKey, endpointPortKey);
    }

    public static string GetRouterOutputPortKeyForEndpointPort(string endpointKey, string endpointPortKey)
    {
        if (string.IsNullOrWhiteSpace(endpointKey))
            return null;

        if (string.IsNullOrWhiteSpace(endpointPortKey)
            || endpointPortKey.Equals(NhdPortKeys.Stream, StringComparison.OrdinalIgnoreCase))
            return endpointKey;

        return string.Format("{0}-out-{1}", endpointKey, endpointPortKey);
    }

    public void ExecuteSwitch(object inputSelector, object outputSelector, eRoutingSignalType signalType)
    {
        // The router's routing ports carry the endpoint device as their selector (see
        // BuildMatrixRouting), so a route from the Essentials routing engine arrives with devices
        // rather than slots. Map them to the slots this method works with.
        if (outputSelector is not NhdMatrixOutput && outputSelector is IKeyed outputDevice
            && OutputSlots.TryGetValue(outputDevice.Key, out var mappedOutput))
            outputSelector = mappedOutput;

        if (inputSelector is not INhdInputSlot && inputSelector is IKeyed inputDevice
            && InputSlots.TryGetValue(inputDevice.Key, out var mappedInput))
            inputSelector = mappedInput;

        if (outputSelector is not NhdMatrixOutput output)
        {
            this.LogError("Output selector is not NhdMatrixOutput");
            return;
        }

        if (inputSelector == null)
        {
            // A released route. Leave the decoder on its current stream rather than blanking it.
            this.LogDebug("Route to '{key}' released; leaving its current stream", output.Key);
            return;
        }

        if (!output.SupportsMatrixSwitching)
        {
            this.LogError("Output '{key}' does not support single-stream matrix routing. Use multiview APIs for multistream decoders.", output.Key);
            return;
        }

        if (inputSelector is not INhdInputSlot inputSlot)
        {
            this.LogError("Input selector is not INhdInputSlot");
            return;
        }

        var handled = false;

        if (_primaryStreamRouter.TryExecute(this, inputSlot, output, signalType, out var primarySignalType))
        {
            handled = true;
            SetTrackedOutputRoutes(output, inputSlot, primarySignalType);
        }

        if (_usbRouter.TryExecute(this, inputSlot, output, signalType))
        {
            handled = true;
            SetTrackedOutputRoutes(output, inputSlot, NhdRoutingSignalTypes.UsbInput | NhdRoutingSignalTypes.UsbOutput | eRoutingSignalType.Usb);
        }

        if (_controlRouter.TryExecute(this, inputSlot, output, signalType))
        {
            handled = true;

            var controlSignalType = (eRoutingSignalType)0;
            if (signalType.HasFlag(NhdRoutingSignalTypes.Ir))
                controlSignalType |= NhdRoutingSignalTypes.Ir;

            if (signalType.HasFlag(NhdRoutingSignalTypes.Serial))
                controlSignalType |= NhdRoutingSignalTypes.Serial;

            if (controlSignalType != 0)
                SetTrackedOutputRoutes(output, inputSlot, controlSignalType);
        }

        if (handled)
            RecordRoute(output, inputSlot, signalType);
        else
            this.LogError("Unsupported signal type '{signalType}' for matrix command", signalType);
    }

    /// <summary>
    /// Clears the route to the given output by routing the "none" sentinel input to it.
    /// The output's entry is removed from <see cref="CurrentRoutes"/> and
    /// <see cref="RouteChanged"/> is raised. Part of <see cref="IRoutingMidpointWithFeedback"/>.
    /// </summary>
    public void ClearRoute(object outputSelector, eRoutingSignalType signalType)
    {
        var output = outputSelector as NhdMatrixOutput;
        if (output == null && outputSelector is RoutingOutputPort outputPort)
            output = OutputSlots.Values.FirstOrDefault(o => ReferenceEquals(o.Device, outputPort.Selector));

        if (output == null)
        {
            this.LogError("ClearRoute: unable to resolve output selector to an NhdMatrixOutput");
            return;
        }

        if (!InputSlots.TryGetValue("none", out var clearInput))
        {
            this.LogError("ClearRoute: no 'none' clear input is available");
            return;
        }

        ExecuteSwitch(clearInput, output, signalType);
    }

    // Maintains the IRoutingMidpointWithFeedback.CurrentRoutes list + raises RouteChanged using
    // this router's own ports (selectors point at the backing NHD endpoint devices).
    private void RecordRoute(NhdMatrixOutput output, INhdInputSlot inputSlot, eRoutingSignalType signalType)
    {
        if (output?.Device == null)
            return;

        var outputPort = SelectRouterPort(OutputPorts, output.Device, signalType);
        if (outputPort == null)
        {
            // The hardware switch already succeeded; surface that feedback couldn't be updated.
            this.LogWarning("RecordRoute: routed output '{rx}' has no matching router output port; CurrentRoutes/RouteChanged will be stale", output.Device.Key);
            return;
        }

        RoutingInputPort inputPort = null;
        if (inputSlot is NhdMatrixInput matrixInput && matrixInput.Device != null)
        {
            inputPort = SelectRouterPort(InputPorts, matrixInput.Device, signalType);
            if (inputPort == null)
                this.LogWarning("RecordRoute: routed input '{tx}' has no matching router input port for '{signalType}'; route feedback may be incomplete", matrixInput.Device.Key, signalType);
        }

        CurrentRoutes.RemoveAll(r => ReferenceEquals(r.OutputPort, outputPort));

        var descriptor = new RouteSwitchDescriptor(outputPort, inputPort);
        if (inputPort != null)
            CurrentRoutes.Add(descriptor);

        RouteChanged?.Invoke(this, descriptor);
    }

    // Pick the router port backing an endpoint device. A transmitter/receiver can expose several
    // ports (stream, IR, serial, USB), all carrying the device as Selector, so prefer the port whose
    // signal type matches the routed signal; fall back to the device's first port.
    private static T SelectRouterPort<T>(IEnumerable<T> ports, IKeyed device, eRoutingSignalType signalType)
        where T : RoutingPort
    {
        var forDevice = ports.Where(p => ReferenceEquals(p.Selector, device)).ToList();
        if (forDevice.Count == 0)
            return null;

        return forDevice.FirstOrDefault(p => (p.Type & signalType) != 0) ?? forDevice[0];
    }

    public void ExecuteNumericSwitch(ushort input, ushort output, eRoutingSignalType type)
    {
        RouteBySlot(input, output, type);
    }

    public void RouteBySlot(int inputSlot, int outputSlot, eRoutingSignalType type)
    {
        if (!TryResolveInputSlot(inputSlot, out var inputSlotObject))
        {
            this.LogError("Unable to find input with matrixInputSlot {slot}", inputSlot);
            return;
        }

        if (!TryResolveOutputSlot(outputSlot, out var outputSlotObject))
        {
            this.LogError("Unable to find output with matrixOutputSlot {slot}", outputSlot);
            return;
        }

        ExecuteSwitch(inputSlotObject, outputSlotObject, type);
    }

    public bool TrySetTrackedMatrixRoute(string txEndpointKey, string rxEndpointKey, eRoutingSignalType signalType)
    {
        if (string.IsNullOrWhiteSpace(rxEndpointKey))
            return false;

        if (!OutputSlots.TryGetValue(rxEndpointKey, out var outputSlot))
            return false;

        if (outputSlot is not NhdMatrixOutput output)
            return false;

        if (!output.SupportsMatrixSwitching)
            return false;

        INhdInputSlot inputSlot = null;
        if (!string.IsNullOrWhiteSpace(txEndpointKey))
        {
            if (!InputSlots.TryGetValue(txEndpointKey, out inputSlot))
                return false;
        }

        SetTrackedOutputRoutes(output, inputSlot, signalType);
        return true;
    }

    public void Route(string inputSlotKey, string outputSlotKey, eRoutingSignalType type)
    {
        if (!TryResolveInputSlot(inputSlotKey, out var inputSlot))
        {
            this.LogError("Unable to find input slot with key {0}", inputSlotKey);
            return;
        }

        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find output slot with key {0}", outputSlotKey);
            return;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            Debug.LogMessage(Serilog.Events.LogEventLevel.Error, "Output with key {key} is not NhdMatrixOutput", this, outputSlotKey);
            return;
        }

        if (!output.SupportsMatrixSwitching)
        {
            this.LogError("Output '{key}' does not support single-stream matrix routing. Use multiview APIs for multistream decoders.", output.Key);
            return;
        }

        ExecuteSwitch(inputSlot, output, type);
    }

    private bool TryResolveInputSlot(string inputSlotKey, out INhdInputSlot inputSlot)
    {
        inputSlot = null;

        if (string.IsNullOrWhiteSpace(inputSlotKey))
            return false;

        if (InputSlots.TryGetValue(inputSlotKey, out inputSlot))
            return true;

        if (!IsClearRouteInputKey(inputSlotKey))
            return false;

        return InputSlots.TryGetValue("none", out inputSlot);
    }

    private bool TryResolveInputSlot(int matrixInputSlot, out INhdInputSlot inputSlot)
    {
        inputSlot = null;

        if (matrixInputSlot <= 0)
            return false;

        var matches = InputSlots.Values
            .OfType<NhdMatrixInput>()
            .Where(slot => slot.SlotNumber == matrixInputSlot)
            .Cast<INhdInputSlot>()
            .ToList();

        if (matches.Count == 0)
            return false;

        if (matches.Count > 1)
        {
            this.LogError("Multiple inputs found with matrixInputSlot {slot}", matrixInputSlot);
            return false;
        }

        inputSlot = matches[0];
        return true;
    }

    private bool TryResolveOutputSlot(int matrixOutputSlot, out NhdMatrixOutput outputSlot)
    {
        outputSlot = null;

        if (matrixOutputSlot <= 0)
            return false;

        var matches = OutputSlots.Values
            .OfType<NhdMatrixOutput>()
            .Where(slot => slot.SlotNumber == matrixOutputSlot)
            .ToList();

        if (matches.Count == 0)
            return false;

        if (matches.Count > 1)
        {
            this.LogError("Multiple outputs found with matrixOutputSlot {slot}", matrixOutputSlot);
            return false;
        }

        if (!matches[0].SupportsMatrixSwitching)
            return false;

        outputSlot = matches[0];
        return true;
    }

    private static bool IsClearRouteInputKey(string inputSlotKey)
    {
        if (string.IsNullOrWhiteSpace(inputSlotKey))
            return false;

        var normalized = inputSlotKey.Trim();
        return normalized.Equals("none", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("off", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(RouteOff, StringComparison.OrdinalIgnoreCase);
    }

    public bool ApplyControllerMVLayout(string outputSlotKey, string layoutName)
    {
        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview preset layouts", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for multiview layout activation");
            return false;
        }

        return ctl.SessionManager.TryActivateMVLayout(this, output.Device, layoutName);
    }

    public bool ActivateMVLayout(string outputSlotKey, string layoutName)
    {
        return ApplyControllerMVLayout(outputSlotKey, layoutName);
    }

    public bool ApplyCustomMVLayout(string outputSlotKey, string layoutKey)
    {
        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview custom layout geometry", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for custom multiview geometry apply");
            return false;
        }

        return ctl.SessionManager.TryApplyCustomMVLayout(this, output.Device, layoutKey);
    }

    public bool ApplyCustomMVLayoutWithContent(
        string outputSlotKey,
        string layoutKey,
        IDictionary<int, string> inputSlotKeysByWindow)
    {
        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview custom layout content apply", output.Device.Key);
            return false;
        }

        var sourceReferencesByWindow = new Dictionary<int, string>();
        foreach (var kvp in inputSlotKeysByWindow ?? new Dictionary<int, string>())
        {
            if (kvp.Key <= 0)
            {
                this.LogError("Invalid window reference '{windowRef}' in custom layout content map", kvp.Key);
                return false;
            }

            if (string.IsNullOrWhiteSpace(kvp.Value))
                continue;

            if (!InputSlots.TryGetValue(kvp.Value, out var inputSlot))
            {
                this.LogError("Unable to find multiview input slot with key {0}", kvp.Value);
                return false;
            }

            if (inputSlot is not NhdMatrixInput input)
            {
                this.LogError("Input slot with key {0} is not NhdMatrixInput", kvp.Value);
                return false;
            }

            sourceReferencesByWindow[kvp.Key] = input.Device.ApiEndpointReference;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for custom multiview content apply");
            return false;
        }

        return ctl.SessionManager.TryApplyCustomMVLayoutWithSources(
            this,
            output.Device,
            layoutKey,
            sourceReferencesByWindow);
    }

    public bool ApplyMVPreset(string outputSlotKey, string presetKey)
    {
        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview preset apply", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for multiview preset apply");
            return false;
        }

        return ctl.SessionManager.TryApplyMVPreset(this, output.Device, presetKey);
    }

    public bool TryGetTrackedMVLayout(string outputSlotKey, out string layoutName, out bool inferred)
    {
        layoutName = null;
        inferred = false;

        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview preset layouts", output.Device.Key);
            return false;
        }

        layoutName = output.Device.ActivePresetMultiviewLayoutName;
        inferred = output.Device.ActivePresetMultiviewLayoutInferred;
        return !string.IsNullOrWhiteSpace(layoutName);
    }

    public bool TryGetTrackedCustomMVLayout(string outputSlotKey, out string layoutKey, out bool inferred)
    {
        layoutKey = null;
        inferred = false;

        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview layouts", output.Device.Key);
            return false;
        }

        layoutKey = output.Device.ActiveCustomMultiviewLayoutKey;
        inferred = output.Device.ActiveCustomMultiviewLayoutInferred;
        return !string.IsNullOrWhiteSpace(layoutKey);
    }

    public bool ProbeAndLearnMVLayouts(string outputSlotKey)
    {
        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview preset layouts", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for multiview layout probing");
            return false;
        }

        return ctl.SessionManager.TryProbeAndLearnMVLayouts(this, output.Device);
    }

    public bool RouteMVTile(string inputSlotKey, string outputSlotKey, int tileReference)
    {
        return RouteMVTile(inputSlotKey, outputSlotKey, null, tileReference);
    }

    public bool FullscreenMVTile(string outputSlotKey, int sourceTileReference)
    {
        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for multiview fullscreen");
            return false;
        }

        return ctl.SessionManager.TryFullscreenMVTile(this, output.Device, sourceTileReference);
    }

    public bool ReturnFromMVFullscreen(string outputSlotKey)
    {
        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for multiview fullscreen return");
            return false;
        }

        return ctl.SessionManager.TryReturnFromMVFullscreen(this, output.Device);
    }

    public bool TryGetMVFullscreenReturnLayout(string outputSlotKey, out string layoutName)
    {
        layoutName = null;

        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for multiview fullscreen return query");
            return false;
        }

        return ctl.SessionManager.TryGetMVFullscreenReturnLayout(output.Device, out layoutName);
    }

    public bool RouteMVTile(string inputSlotKey, string outputSlotKey, string layoutName, int tileReference)
    {
        if (!InputSlots.TryGetValue(inputSlotKey, out var inputSlot))
        {
            this.LogError("Unable to find multiview input slot with key {0}", inputSlotKey);
            return false;
        }

        if (!OutputSlots.TryGetValue(outputSlotKey, out var outputSlot))
        {
            this.LogError("Unable to find multiview output slot with key {0}", outputSlotKey);
            return false;
        }

        if (inputSlot is not NhdMatrixInput input)
        {
            this.LogError("Input slot with key {0} is not NhdMatrixInput", inputSlotKey);
            return false;
        }

        if (outputSlot is not NhdMatrixOutput output)
        {
            this.LogError("Output slot with key {0} is not NhdMatrixOutput", outputSlotKey);
            return false;
        }

        if (!output.Device.SupportsMultiview)
        {
            this.LogError("Endpoint '{key}' does not support multiview tile routing", output.Device.Key);
            return false;
        }

        var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
        if (ctl?.SessionManager == null)
        {
            this.LogError("NHD-CTL session manager is not available for guarded multiview tile routing");
            return false;
        }

        var result = ctl.SessionManager.RouteMVTileGuarded(this, input.Device, output.Device, layoutName, tileReference);

        var effectiveLayout = string.IsNullOrWhiteSpace(layoutName)
            ? output.Device.ActivePresetMultiviewLayoutName
            : layoutName;

        switch (result)
        {
            case MultiviewTileRouteResult.Queued:
                this.LogInformation(
                    "Multiview tile route queued pending state verification. tx='{tx}', rx='{rx}', layout='{layout}', tile={tile}",
                    input.Device.Key,
                    output.Device.Key,
                    effectiveLayout,
                    tileReference);
                break;

            case MultiviewTileRouteResult.Rejected:
                this.LogError(
                    "Multiview tile route rejected as invalid. tx='{tx}', rx='{rx}', layout='{layout}', tile={tile}. See preceding error for the specific reason.",
                    input.Device.Key,
                    output.Device.Key,
                    effectiveLayout,
                    tileReference);
                break;
        }

        return result != MultiviewTileRouteResult.Rejected;
    }

    private void BuildMatrixRouting()
    {
        try
        {
            var transmitters = DeviceManager
                .AllDevices.OfType<NhdBaseDevice>()
                .Where(d => d.IsTransmitter)
                .ToList();

            var mockTransmitters = DeviceManager
                .AllDevices.OfType<MockNhdTx>()
                .ToList();

            var receiverSlots = DeviceManager
                .AllDevices.OfType<NhdBaseDevice>()
                .Where(IsMatrixOutputSlotCandidate)
                .ToList();

            var matrixTieLineReceivers = receiverSlots
                .Where(IsMatrixTieLineReceiver)
                .ToList();

            InputSlots = transmitters
                .Select(d => new NhdMatrixInput(d))
                .Cast<INhdInputSlot>()
                .ToDictionary(i => i.Key, i => i);

            foreach (var mockTx in mockTransmitters)
            {
                var mockInput = new MockNhdMatrixInput(mockTx);
                InputSlots[mockInput.Key] = mockInput;
            }

            this.LogDebug("Total inputs: {count}", InputSlots.Count);

            OutputSlots = receiverSlots
                .Select(d => new NhdMatrixOutput(d))
                .ToDictionary(o => o.Key, o => o);

            this.LogDebug("Total outputs: {count}", OutputSlots.Count);

            var clearSupportedSignals = OutputSlots.Values
                .Aggregate((eRoutingSignalType)0, (current, output) => current | output.SupportedSignalTypes);

            var clearInput = new NhdMatrixClearInput(clearSupportedSignals);
            InputSlots.Add(clearInput.Key, clearInput);

            // Build router ports so tie lines can connect to them for each available endpoint routing port.
            foreach (var tx in transmitters)
                AddInputPortsForTransmitter(tx.Key, tx.OutputPorts, tx);

            // Mock transmitters (encoders) get the exact same router-input-port treatment as real
            // ones, so config/dev-tools consumers can address them uniformly through NhdRouter.
            foreach (var mockTx in mockTransmitters)
                AddInputPortsForTransmitter(mockTx.Key, mockTx.OutputPorts, mockTx);

            foreach (var rx in matrixTieLineReceivers)
            {
foreach (var endpointInputPort in rx.InputPorts)
{
    if (endpointInputPort == null)
        continue;

    var routerPortKey = GetRouterOutputPortKeyForEndpointPort(rx.Key, endpointInputPort.Key);
    if (string.IsNullOrWhiteSpace(routerPortKey))
        continue;

                    if (OutputPorts.Any(p => string.Equals(p.Key, routerPortKey, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    OutputPorts.Add(new RoutingOutputPort(
                        routerPortKey,
                        endpointInputPort.Type,
                        endpointInputPort.ConnectionType,
                        rx,
                        this));
                }
            }

            // Multiview decoders (real Nhd150Rx and mock MockNhdRx alike, via the shared
            // IRoutingSinkWithLayouts interface) aren't addressable as a single output above - each
            // window tile is independently routable, so the router gets one output port per tile
            // instead, keyed to match the tile sink's own DeviceManager key ("{rxKey}-tile{N}").
            foreach (var layoutParent in DeviceManager.AllDevices.OfType<IRoutingSinkWithLayouts>())
            {
                foreach (var tileSink in layoutParent.WindowTileSinks.Values)
                {
                    if (tileSink is not IKeyed keyedTile || string.IsNullOrEmpty(keyedTile.Key))
                        continue;

                    var routerPortKey = keyedTile.Key;
                    if (OutputPorts.Any(p => string.Equals(p.Key, routerPortKey, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var tilePort = tileSink.InputPorts.FirstOrDefault();
                    var tileSignalType = tilePort?.Type ?? eRoutingSignalType.AudioVideo;
                    var tileConnectionType = tilePort?.ConnectionType ?? eRoutingPortConnectionType.Streaming;

                    OutputPorts.Add(new RoutingOutputPort(
                        routerPortKey,
                        tileSignalType,
                        tileConnectionType,
                        tileSink,
                        this));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogMessage(ex, "Exception building MatrixRouting: {message}", this, ex.Message);
        }
    }

    private void AddInputPortsForTransmitter(string endpointKey, RoutingPortCollection<RoutingOutputPort> endpointOutputPorts, IKeyed selector)
    {
foreach (var endpointOutputPort in endpointOutputPorts)
{
    if (endpointOutputPort == null)
        continue;

    var routerPortKey = GetRouterInputPortKeyForEndpointPort(endpointKey, endpointOutputPort.Key);
    if (string.IsNullOrWhiteSpace(routerPortKey))
                continue;

            if (InputPorts.Any(p => string.Equals(p.Key, routerPortKey, StringComparison.OrdinalIgnoreCase)))
                continue;

            InputPorts.Add(new RoutingInputPort(
                routerPortKey,
                endpointOutputPort.Type,
                endpointOutputPort.ConnectionType,
                selector,
                this));
        }
    }

    private void BuildTieLines()
    {
        try
        {
            var transmitters = DeviceManager.AllDevices.OfType<NhdBaseDevice>().Where(d => d.IsTransmitter).ToList();
            NhdTieLineConnector.AddTieLinesForTransmitters(transmitters);

            // NOTE: mock transmitters and multiview tiles deliberately do NOT get equivalent tie
            // lines here (see AddTieLinesForTransmitters(IEnumerable<MockNhdTx>) and
            // AddTieLinesForTiles below, both still present but unused) - see the comment on
            // AddTieLinesForTiles for why wiring them up crashes Essentials at boot.

            var receiverCandidates = DeviceManager
                .AllDevices.OfType<NhdBaseDevice>()
                .Where(IsMatrixOutputSlotCandidate)
                .ToList();

            var skippedMultiviewReceivers = receiverCandidates
                .Where(d => d.SupportsMultiview)
                .ToList();

            if (skippedMultiviewReceivers.Count > 0)
            {
                this.LogInformation(
                    "Skipping multiview receivers from matrix tie-line generation at startup. count={count}, receivers='{receivers}'",
                    skippedMultiviewReceivers.Count,
                    string.Join(",", skippedMultiviewReceivers.Select(d => d.Key)));
            }

            var receivers = receiverCandidates
                .Where(IsMatrixTieLineReceiver)
                .ToList();

            NhdTieLineConnector.AddTieLinesForReceivers(receivers);

            // Deliberately NOT calling NhdTieLineConnector.AddTieLinesForTiles here - see its doc
            // comment for why tying tiles through the router blows up boot-time route mapping.
        }
        catch (Exception ex)
        {
            Debug.LogMessage(ex, "Exception building tie lines: {message}", null, ex.Message);
        }
    }

    private static void SetTrackedOutputRoutes(NhdMatrixOutput output, INhdInputSlot inputSlot, eRoutingSignalType signalType)
    {
        if (output == null)
            return;

        var trackedInput = inputSlot is NhdMatrixClearInput
            ? null
            : inputSlot;

        var hasVideo = signalType.HasFlag(eRoutingSignalType.Video);
        var hasAudio = signalType.HasFlag(eRoutingSignalType.Audio);
        if (signalType == eRoutingSignalType.AudioVideo || (hasVideo && hasAudio))
        {
            output.SetInputRoute(eRoutingSignalType.Video, trackedInput);
            output.SetInputRoute(eRoutingSignalType.Audio, trackedInput);
        }
        else
        {
            if (hasVideo)
                output.SetInputRoute(eRoutingSignalType.Video, trackedInput);

            if (hasAudio)
                output.SetInputRoute(eRoutingSignalType.Audio, trackedInput);
        }

        if (
            signalType.HasFlag(NhdRoutingSignalTypes.UsbInput)
            || signalType.HasFlag(NhdRoutingSignalTypes.UsbOutput)
            || signalType.HasFlag(eRoutingSignalType.Usb))
        {
            output.SetInputRoute(NhdRoutingSignalTypes.UsbInput, trackedInput);
            output.SetInputRoute(NhdRoutingSignalTypes.UsbOutput, trackedInput);

            if (Enum.IsDefined(typeof(eRoutingSignalType), "Usb"))
                output.SetInputRoute(eRoutingSignalType.Usb, trackedInput);
        }

        if (signalType.HasFlag(NhdRoutingSignalTypes.Ir))
            output.SetInputRoute(NhdRoutingSignalTypes.Ir, trackedInput);

        if (signalType.HasFlag(NhdRoutingSignalTypes.Serial))
            output.SetInputRoute(NhdRoutingSignalTypes.Serial, trackedInput);
    }

    private static string GetTxReference(INhdInputSlot inputSlot)
    {
        if (inputSlot == null || inputSlot is NhdMatrixClearInput)
            return RouteOff;

        return inputSlot is NhdMatrixInput matrixInput
            ? matrixInput.Device.ApiEndpointReference
            : RouteOff;
    }

    private static bool IsMatrixOutputSlotCandidate(NhdBaseDevice device)
    {
        return device != null
            && !device.IsTransmitter
            && device is not NhdCtlPro;
    }

    private static bool IsMatrixTieLineReceiver(NhdBaseDevice device)
    {
        return IsMatrixOutputSlotCandidate(device);
    }

    private sealed class NhdPrimaryStreamDomainRouter
    {
        public bool TryExecute(IKeyed source, INhdInputSlot inputSlot, NhdMatrixOutput output, eRoutingSignalType signalType, out eRoutingSignalType routedSignalType)
        {
            routedSignalType = 0;

            if (!TryResolveStreamSignalType(signalType, out routedSignalType))
                return false;

            var txRef = GetTxReference(inputSlot);
            var rxRef = output.Device.ApiEndpointReference;
            var prefix = routedSignalType == eRoutingSignalType.AudioVideo
                ? "matrix set"
                : routedSignalType == eRoutingSignalType.Video
                    ? "matrix video set"
                    : "matrix audio set";

            NhdApiCommandSender.TrySend(source, $"{prefix} {txRef} {rxRef}");
            return true;
        }

        private static bool TryResolveStreamSignalType(eRoutingSignalType signalType, out eRoutingSignalType streamType)
        {
            streamType = 0;

            if (signalType == eRoutingSignalType.AudioVideo)
            {
                streamType = eRoutingSignalType.AudioVideo;
                return true;
            }

            var hasVideo = signalType.HasFlag(eRoutingSignalType.Video);
            var hasAudio = signalType.HasFlag(eRoutingSignalType.Audio);

            if (hasVideo && hasAudio)
            {
                streamType = eRoutingSignalType.AudioVideo;
                return true;
            }

            if (hasVideo)
            {
                streamType = eRoutingSignalType.Video;
                return true;
            }

            if (hasAudio)
            {
                streamType = eRoutingSignalType.Audio;
                return true;
            }

            return false;
        }
    }

    private sealed class NhdUsbDomainRouter
    {
        public bool TryExecute(IKeyed source, INhdInputSlot inputSlot, NhdMatrixOutput output, eRoutingSignalType signalType)
        {
            if (!HasUsbSignal(signalType))
                return false;

            var txRef = GetTxReference(inputSlot);
            var rxRef = output.Device.ApiEndpointReference;

            NhdApiCommandSender.TrySend(source, $"matrix usb set {txRef} {rxRef}");
            return true;
        }

        private static bool HasUsbSignal(eRoutingSignalType signalType)
        {
            return signalType.HasFlag(NhdRoutingSignalTypes.UsbInput)
                || signalType.HasFlag(NhdRoutingSignalTypes.UsbOutput)
                || signalType.HasFlag(eRoutingSignalType.Usb);
        }
    }

    private sealed class NhdControlDomainRouter
    {
        public bool TryExecute(IKeyed source, INhdInputSlot inputSlot, NhdMatrixOutput output, eRoutingSignalType signalType)
        {
            var handled = false;
            var txRef = GetTxReference(inputSlot);
            var rxRef = output.Device.ApiEndpointReference;

            if (signalType.HasFlag(NhdRoutingSignalTypes.Ir))
            {
                NhdApiCommandSender.TrySend(source, $"matrix infrared set {txRef} {rxRef}");
                handled = true;
            }

            if (signalType.HasFlag(NhdRoutingSignalTypes.Serial))
            {
                NhdApiCommandSender.TrySend(source, $"matrix serial set {txRef} {rxRef}");
                handled = true;
            }

            return handled;
        }
    }
}
