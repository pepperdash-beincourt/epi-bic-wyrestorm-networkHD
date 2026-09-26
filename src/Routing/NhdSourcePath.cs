using System;
using System.Collections.Generic;
using System.Linq;
using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace PepperDash.Essentials.Plugin.Routing;

/// <summary>
/// Finds the NetworkHD transmitter that carries a source, following configuration tie lines
/// through any switching devices ahead of the encoder (for example a document camera with its
/// own HDMI inputs, or a small switcher), and selects that path when asked.
/// </summary>
/// <remarks>
/// Multiview layouts refer to tiles by source device key and are sent straight to the NetworkHD
/// controller, so they never pass through the Essentials routing engine. Without this, a source
/// behind another switching device resolves to no transmitter, and nothing switches that device
/// to the source either. Only devices outside NetworkHD are traversed: the walk stops at the
/// first transmitter and never enters NetworkHD endpoints or the global router.
/// </remarks>
internal static class NhdSourcePath
{
    private const int MaxHops = 8;

    /// <summary>One switching device on the way to the transmitter, and the ports it connects.</summary>
    internal sealed class Hop
    {
        public Hop(IRoutingMidpointWithFeedback device, RoutingInputPort input, RoutingOutputPort output)
        {
            Device = device;
            Input = input;
            Output = output;
        }

        public IRoutingMidpointWithFeedback Device { get; }
        public RoutingInputPort Input { get; }
        public RoutingOutputPort Output { get; }
    }

    /// <summary>
    /// Resolves a source device key to the transmitter that carries it. A transmitter key resolves
    /// to itself.
    /// </summary>
    public static NhdBaseDevice FindTransmitter(string sourceKey)
    {
        return FindTransmitter(sourceKey, new List<Hop>());
    }

    /// <summary>
    /// Resolves a source device key to its transmitter and fills <paramref name="path"/> with the
    /// switching devices in between, source side first.
    /// </summary>
    public static NhdBaseDevice FindTransmitter(string sourceKey, List<Hop> path)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
            return null;

        var trimmed = sourceKey.Trim();

        if (DeviceManager.GetDeviceForKey(trimmed) is NhdBaseDevice direct && direct.IsTransmitter)
            return direct;

        return Walk(trimmed, null, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
    }

    /// <summary>
    /// Switches every device between the source and its transmitter to the source's input. Devices
    /// already on that input are left alone, so repeated layout refreshes do not resend commands.
    /// </summary>
    /// <returns>The transmitter carrying the source, or null when none is reachable.</returns>
    public static NhdBaseDevice SelectPath(string sourceKey, IKeyed requestedBy)
    {
        var path = new List<Hop>();
        var tx = FindTransmitter(sourceKey, path);

        foreach (var hop in path)
        {
            var alreadySelected = hop.Device.CurrentRoutes != null && hop.Device.CurrentRoutes.Any(r =>
                r != null && ReferenceEquals(r.InputPort, hop.Input) && ReferenceEquals(r.OutputPort, hop.Output));

            if (alreadySelected)
                continue;

            Debug.LogInformation("[{0}] Selecting '{1}' on '{2}' for source '{3}'",
                requestedBy?.Key ?? "NhdSourcePath", hop.Input.Key, hop.Device.Key, sourceKey);

            try
            {
                hop.Device.ExecuteSwitch(hop.Input.Selector, hop.Output.Selector, eRoutingSignalType.AudioVideo);
            }
            catch (Exception ex)
            {
                Debug.LogError("[{0}] Switching '{1}' for source '{2}' failed: {3}",
                    requestedBy?.Key ?? "NhdSourcePath", hop.Device.Key, sourceKey, ex.Message);
            }
        }

        return tx;
    }

    private static NhdBaseDevice Walk(string deviceKey, RoutingInputPort enteredVia, List<Hop> path,
        HashSet<string> visited, int depth)
    {
        if (depth > MaxHops || !visited.Add(deviceKey))
            return null;

        foreach (var tieLine in TieLineCollection.Default)
        {
            var from = tieLine?.SourcePort?.ParentDevice;
            var to = tieLine?.DestinationPort?.ParentDevice;
            if (from == null || to == null)
                continue;

            if (!string.Equals(from.Key, deviceKey, StringComparison.OrdinalIgnoreCase))
                continue;

            // Leaving a switching device through this output: record which input it must select.
            var hopAdded = false;
            if (enteredVia != null && from is IRoutingMidpointWithFeedback switcher)
            {
                path.Add(new Hop(switcher, enteredVia, tieLine.SourcePort));
                hopAdded = true;
            }

            if (to is NhdBaseDevice endpoint)
            {
                if (endpoint.IsTransmitter)
                    return endpoint;
            }
            else if (to is IRoutingMidpointWithFeedback && !(to is NhdGlobalRouter))
            {
                var found = Walk(to.Key, tieLine.DestinationPort, path, visited, depth + 1);
                if (found != null)
                    return found;
            }

            if (hopAdded)
                path.RemoveAt(path.Count - 1);
        }

        return null;
    }
}
