using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Plugin.Config;
using PepperDash.Essentials.Plugin.Enums;
using PepperDash.Essentials.Plugin.Routing;

namespace PepperDash.Essentials.Plugin.Comms
{
    public sealed class NhdCtlSessionReadyStateChangedEventArgs : EventArgs
    {
        public NhdCtlSessionReadyStateChangedEventArgs(bool isReady, string reason)
        {
            IsReady = isReady;
            Reason = reason;
        }

        public bool IsReady { get; private set; }
        public string Reason { get; private set; }
    }

    /// <summary>
    /// Outcome of a multiview tile route request.
    /// </summary>
    public enum MultiviewTileRouteResult
    {
        /// <summary>The route command was sent to the controller immediately.</summary>
        Sent,

        /// <summary>The route was deferred until the decoder's multiview state is verified, then auto-dispatched.</summary>
        Queued,

        /// <summary>The route was rejected as invalid (e.g. tile out of range, layout unavailable) and will not be sent.</summary>
        Rejected,
    }

    public class NhdCtlSessionManager
    {
        private static readonly Regex AliasLineRegex = new Regex(
            "^(?<hostname>\\S+?)(?:'s|’s)\\s+alias\\s+is\\s+(?<alias>\\S+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EndpointNotifyRegex = new Regex(
            "^notify\\s+endpoint\\s+(?<state>[+-])\\s+(?<reference>\\S+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SinkNotifyRegex = new Regex(
            "^notify\\s+sink\\s+(?<state>[+-]|found|lost|on|off|sync|nosync|present|absent|online|offline|1|0)\\s+(?<reference>\\S+)(?:\\s+\\(.*\\))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VideoNotifyRegex = new Regex(
            "^notify\\s+video\\s+(?:(?<state1>[+-]|found|lost|on|off|sync|nosync|present|absent|online|offline|1|0)\\s+)?(?<reference>\\S+?)(?:\\s+(?<state2>[+-]|found|lost|on|off|sync|nosync|present|absent|online|offline|1|0))?(?:\\s+\\(.*\\))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DeviceStatusKeyValueRegex = new Regex(
            "^\"(?<key>[^\"]+)\"\\s*:\\s*(?:\"(?<value>[^\"]*)\"|(?<valueBare>[^,\\s\\}]+))\\s*,?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MviewInformationLineRegex = new Regex(
            "^(?<reference>\\S+)\\s+(?<mode>tile|overlay)\\s*(?<tiles>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TileDescriptorRegex = new Regex(
            "^(?<source>[^:\\s]+):(?<x>\\d+)_(?<y>\\d+)_(?<w>\\d+)_(?<h>\\d+):(?<scale>fit|stretch)(?::\\d+)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MsceneActiveResponseRegex = new Regex(
            "^mscene\\s+active\\s+(?<reference>\\S+)\\s+(?<layout>\\S+)\\s+(?<result>success|failure)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MsceneChangeResponseRegex = new Regex(
            "^mscene\\s+change\\s+(?<reference>\\S+)\\s+(?<layout>\\S+)\\s+(?<tile>\\d+)\\s+(?<source>\\S+)\\s+(?<result>success|failure)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MsceneSetAudioResponseRegex = new Regex(
            "^mscene\\s+set\\s+audio\\s+(?<reference>\\S+)\\s+(?<layout>\\S+)\\s+(?<mode>window|separate)\\s+(?<target>\\S+)\\s+(?<result>success|failure)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MviewSetAudioResponseRegex = new Regex(
            "^mview\\s+set\\s+audio\\s+(?<reference>\\S+)\\s+separate\\s+(?<source>\\S+)\\s+(?<result>success|failure)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MatrixInformationHeaderRegex = new Regex(
            "^matrix(?:\\s+(?<domain>video|audio|usb|serial|infrared))?\\s+information:$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MatrixSetResponseRegex = new Regex(
            "^matrix(?:\\s+(?<domain>video|audio|usb|serial|infrared))?\\s+set\\s+(?<tx>\\S+)\\s+(?<rx>\\S+)\\s+(?<result>success|failure)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TelnetNegotiationRegex = new Regex(
            "\\[(?:[0-9A-F]{2}h)\\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TelnetHexTokenRegex = new Regex(
            "\\[(?<hex>[0-9A-F]{2})h\\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const byte TelnetIac = 0xFF;
        private const byte TelnetWill = 0xFB;
        private const byte TelnetWont = 0xFC;
        private const byte TelnetDo = 0xFD;
        private const byte TelnetDont = 0xFE;

        private const byte TelnetOptionEcho = 0x01;
        private const byte TelnetOptionSuppressGoAhead = 0x03;

        private static readonly TimeSpan MultiviewStateFreshness = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MultiviewRefreshThrottle = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MatrixRefreshThrottle = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MatrixPeriodicRefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MsceneListRefreshThrottle = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PendingTileRouteExpiry = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PendingFullscreenRequestExpiry = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FullscreenRouteClearBypassWindow = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan VideoLostNotifyDebounce = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DeviceStatusRefreshThrottle = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SessionProbeThrottle = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CredentialPromptThrottle = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan LoginFailureThrottle = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan PromptDedupWindow = TimeSpan.FromMilliseconds(10);

        private sealed class PendingMultiviewTileRoute
        {
            public string TxEndpointKey { get; set; }
            public string LayoutName { get; set; }
            public int TileReference { get; set; }
            public string RequestedByKey { get; set; }
            public DateTime QueuedUtc { get; set; }
        }

        private sealed class PendingLayoutProbe
        {
            public string EndpointKey { get; set; }
            public Queue<string> RemainingLayouts { get; set; }
            public string ActiveLayout { get; set; }
            public int AttemptedCount { get; set; }
            public int LearnedCount { get; set; }
            public string RequestedByKey { get; set; }
        }

        private sealed class PendingFullscreenRequest
        {
            public int SourceTileReference { get; set; }
            public string RequestedByKey { get; set; }
            public DateTime QueuedUtc { get; set; }
        }

        private sealed class StartupProbeRestoreState
        {
            public string OriginalLayoutName { get; set; }
            public string OriginalGeometrySignature { get; set; }
            public string MatchedOriginalLayoutName { get; set; }
        }

        private sealed class PendingMultiviewFullscreen
        {
            public string PreviousLayoutName { get; set; }
            public int SourceTileReference { get; set; }
            public string SourceReference { get; set; }
            public DateTime QueuedUtc { get; set; }
        }

        private sealed class MultiviewFullscreenReturnState
        {
            public string PreviousLayoutName { get; set; }
            public DateTime CapturedUtc { get; set; }
        }

        private sealed class RecentFullscreenRoute
        {
            public string TxReference { get; set; }
            public string LayoutName { get; set; }
            public int TileReference { get; set; }
            public DateTime RequestedUtc { get; set; }
        }

        private sealed class PendingDeviceStatusEntry
        {
            public string Alias { get; set; }
            public string Name { get; set; }
            public string HdmiOutResolution { get; set; }
            public bool? IsOnline { get; set; }
        }

        private readonly NhdCtlPro _ctl;
        private readonly CommunicationGather _gather;
        private readonly Dictionary<string, PendingMultiviewTileRoute> _pendingTileRoutes = new Dictionary<string, PendingMultiviewTileRoute>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastMsceneListRequestUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _pendingLayoutGeometryCapture = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingLayoutProbe> _pendingLayoutProbes = new Dictionary<string, PendingLayoutProbe>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingFullscreenRequest> _pendingFullscreenRequests = new Dictionary<string, PendingFullscreenRequest>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StartupProbeRestoreState> _startupProbeRestoreStates = new Dictionary<string, StartupProbeRestoreState>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _startupProbeCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingMultiviewFullscreen> _pendingFullscreen = new Dictionary<string, PendingMultiviewFullscreen>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _pendingFullscreenReturns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MultiviewFullscreenReturnState> _fullscreenReturnStates = new Dictionary<string, MultiviewFullscreenReturnState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RecentFullscreenRoute> _recentFullscreenRoutes = new Dictionary<string, RecentFullscreenRoute>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _pendingCustomWindowAudioApplies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Timer> _pendingVideoLostDebounceTimers = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        private readonly object _videoLostDebounceLock = new object();
        private readonly HashSet<string> _subscribedNotificationReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Timer _periodicMatrixRefreshTimer;
        private DateTime? _lastMatrixRefreshUtc;
        private DateTime? _lastSessionProbeUtc;
        private DateTime? _lastUserPromptHandledUtc;
        private DateTime? _lastPasswordPromptHandledUtc;
        private DateTime? _lastLoginFailureHandledUtc;
        private DateTime? _lastStandaloneUserPromptSeenUtc;
        private DateTime? _lastStandalonePasswordPromptSeenUtc;
        private bool _isSessionReady;
        private bool _bootstrapPending;
        private bool _telnetAwaitingCommand;
        private bool _telnetAwaitingOption;
        private byte _telnetPendingCommand;

        private bool _isParsingMviewInformation;
        private bool _isParsingMatrixInformation;
        private bool _isParsingMsceneList;
        private bool _isParsingDeviceStatus;
        private int _deviceStatusBraceDepth;
        private PendingDeviceStatusEntry _pendingDeviceStatusEntry;
        private readonly HashSet<string> _deviceStatusSeenEndpointKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private NhdBaseDevice _pendingMviewEndpoint;
        private NhdMultiStreamMode _pendingMviewMode;
        private readonly List<NhdMultiviewTileState> _pendingMviewTiles = new List<NhdMultiviewTileState>();
        private eRoutingSignalType _pendingMatrixSignalType = eRoutingSignalType.AudioVideo;
        private DateTime? _lastDeviceStatusRefreshUtc;

        public NhdCtlSessionManager(NhdCtlPro ctl)
        {
            _ctl = ctl;
            // Accept both LF and CRLF responses from the CTL CLI.
            _gather = new CommunicationGather(ctl.Comms, "\n");
            _gather.LineReceived += HandleLineReceived;
            _ctl.Comms.TextReceived += HandleRawTextReceived;
        }

        public event EventHandler<NhdCtlSessionReadyStateChangedEventArgs> SessionReadyStateChanged;

        public bool IsReadyForApiCommands => _isSessionReady;

        public void ProbeSessionHealth(string reason = null)
        {
            SendSessionProbe(reason ?? "health probe");
        }

        public void HandleCtlTransportConnectionChanged(bool isConnected)
        {
            if (!isConnected)
            {
                ArmBootstrap("transport disconnected");
                MarkAllEndpointsOffline("transport disconnected");
                return;
            }

            // When transport returns, probe so readiness can be re-established and bootstrap replayed.
            SendSessionProbe("transport connected");
        }

        public void StartSessionLifecycle()
        {
            ArmBootstrap("startup");
            StartPeriodicMatrixRefresh();
            SendSessionProbe("startup");
        }

        public void StopSessionLifecycle()
        {
            _periodicMatrixRefreshTimer?.Dispose();
            _periodicMatrixRefreshTimer = null;

            _gather.LineReceived -= HandleLineReceived;

            if (_ctl?.Comms != null)
            {
                _ctl.Comms.TextReceived -= HandleRawTextReceived;
            }

            lock (_videoLostDebounceLock)
            {
                foreach (var timer in _pendingVideoLostDebounceTimers.Values)
                {
                    timer?.Dispose();
                }

                _pendingVideoLostDebounceTimers.Clear();
            }
        }

        public bool TryRouteMVTile(IKeyed requestedBy, NhdBaseDevice txEndpoint, NhdBaseDevice rxEndpoint, string layoutName, int tileReference)
        {
            return RouteMVTileGuarded(requestedBy, txEndpoint, rxEndpoint, layoutName, tileReference) == MultiviewTileRouteResult.Sent;
        }

        public MultiviewTileRouteResult RouteMVTileGuarded(IKeyed requestedBy, NhdBaseDevice txEndpoint, NhdBaseDevice rxEndpoint, string layoutName, int tileReference)
        {
            var source = requestedBy ?? _ctl;

            if (txEndpoint == null || rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to route multiview tile: TX or RX endpoint is null", source.Key);
                return MultiviewTileRouteResult.Rejected;
            }

            if (!rxEndpoint.SupportsMultiview)
            {
                Debug.LogError("[{0}] Endpoint '{1}' does not support multiview tile routing", source.Key, rxEndpoint.Key);
                return MultiviewTileRouteResult.Rejected;
            }

            var trimmedLayout = string.IsNullOrWhiteSpace(layoutName)
                ? rxEndpoint.ActivePresetMultiviewLayoutName
                : layoutName.Trim();

            if (string.IsNullOrWhiteSpace(trimmedLayout))
            {
                Debug.LogError("[{0}] Multiview tile routing requires an active preset layout. Activate one first with 'mscene active'.", source.Key);
                return MultiviewTileRouteResult.Rejected;
            }

            if (tileReference <= 0)
            {
                Debug.LogError("[{0}] Multiview tile reference must be >= 1", source.Key);
                return MultiviewTileRouteResult.Rejected;
            }

            if (rxEndpoint.AvailablePresetMultiviewLayouts.Count > 0 && !rxEndpoint.IsKnownPresetMVLayout(trimmedLayout))
            {
                Debug.LogError("[{0}] Multiview preset layout '{1}' is not available on endpoint '{2}'", source.Key, trimmedLayout, rxEndpoint.Key);
                return MultiviewTileRouteResult.Rejected;
            }

            if (NhdBaseDevice.TryInferPresetLayoutShape(trimmedLayout, out var inferredTileCount, out _) && tileReference > inferredTileCount)
            {
                Debug.LogError("[{0}] Multiview tile reference {1} exceeds tile count {2} for layout '{3}'", source.Key, tileReference, inferredTileCount, trimmedLayout);
                return MultiviewTileRouteResult.Rejected;
            }

            if (CanRouteTileNow(rxEndpoint, tileReference))
            {
                var command = BuildPresetTileRouteCommand(
                    txEndpoint.ApiEndpointReference,
                    rxEndpoint.ApiEndpointReference,
                    trimmedLayout,
                    tileReference);

                var sent = NhdApiCommandSender.TrySend(source, command);
                if (!sent)
                {
                    Debug.LogError("[{0}] Failed to send multiview tile route command for endpoint '{1}'", source.Key, rxEndpoint.Key);
                    return MultiviewTileRouteResult.Rejected;
                }

                if (!ShouldBypassFullscreenReturnClearForRoute(rxEndpoint, txEndpoint.ApiEndpointReference, trimmedLayout, tileReference))
                {
                    ClearFullscreenReturnState(rxEndpoint, "tile route changed");
                }

                return MultiviewTileRouteResult.Sent;
            }

            _pendingTileRoutes[rxEndpoint.Key] = new PendingMultiviewTileRoute
            {
                TxEndpointKey = txEndpoint.Key,
                LayoutName = trimmedLayout,
                TileReference = tileReference,
                RequestedByKey = source.Key,
                QueuedUtc = DateTime.UtcNow,
            };

            RequestMultiviewState(rxEndpoint, source);
            return MultiviewTileRouteResult.Queued;
        }

        public bool TryActivateMVLayout(IKeyed requestedBy, NhdBaseDevice rxEndpoint, string layoutName)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to activate multiview layout: RX endpoint is null", source.Key);
                return false;
            }

            if (!rxEndpoint.SupportsMultiview)
            {
                Debug.LogError("[{0}] Endpoint '{1}' does not support multiview preset layouts", source.Key, rxEndpoint.Key);
                return false;
            }

            if (string.IsNullOrWhiteSpace(layoutName))
            {
                Debug.LogError("[{0}] Multiview preset layout name cannot be empty", source.Key);
                return false;
            }

            var trimmedLayout = layoutName.Trim();

            if (rxEndpoint.AvailablePresetMultiviewLayouts.Count > 0 && !rxEndpoint.IsKnownPresetMVLayout(trimmedLayout))
            {
                Debug.LogError("[{0}] Multiview preset layout '{1}' is not available on endpoint '{2}'", source.Key, trimmedLayout, rxEndpoint.Key);
                return false;
            }

            var command = $"mscene active {rxEndpoint.ApiEndpointReference} {trimmedLayout}";

            return NhdApiCommandSender.TrySend(source, command);
        }

        public bool TryApplyCustomMVLayout(IKeyed requestedBy, NhdBaseDevice rxEndpoint, string layoutKey)
        {
            return TryApplyCustomMVLayoutWithSources(requestedBy, rxEndpoint, layoutKey, null);
        }

        public bool TryApplyCustomMVLayoutWithSources(
            IKeyed requestedBy,
            NhdBaseDevice rxEndpoint,
            string layoutKey,
            IDictionary<int, string> sourceReferencesByWindow)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to apply custom multiview layout: RX endpoint is null", source.Key);
                return false;
            }

            if (!rxEndpoint.SupportsMultiview)
            {
                Debug.LogError("[{0}] Endpoint '{1}' does not support multiview custom layout geometry", source.Key, rxEndpoint.Key);
                return false;
            }

            if (string.IsNullOrWhiteSpace(layoutKey))
            {
                Debug.LogError("[{0}] Custom multiview layout key cannot be empty", source.Key);
                return false;
            }

            if (!rxEndpoint.TryGetCustomMVLayout(layoutKey, out var layout))
            {
                Debug.LogError("[{0}] Custom multiview layout '{1}' is not defined for endpoint '{2}'", source.Key, layoutKey, rxEndpoint.Key);
                return false;
            }

            if (!TryBuildScaledCustomLayoutCommand(rxEndpoint, layout, sourceReferencesByWindow, out var command, out _, out _, out _))
            {
                Debug.LogError("[{0}] Custom multiview layout '{1}' on endpoint '{2}' has invalid geometry", source.Key, layout.Key, rxEndpoint.Key);
                return false;
            }

            if (!TryValidateCustomLayoutAudioMetadata(rxEndpoint, layout, out var audioValidationError))
            {
                Debug.LogError("[{0}] Custom multiview layout '{1}' on endpoint '{2}' has invalid audio metadata: {3}", source.Key, layout.Key, rxEndpoint.Key, audioValidationError);
                return false;
            }

            var sent = NhdApiCommandSender.TrySend(source, command);
            if (sent)
            {
                if (!TryApplyCustomLayoutAudioMetadata(source, rxEndpoint, layout))
                {
                    return false;
                }

                RequestMultiviewState(rxEndpoint, source, force: true);
            }

            return sent;
        }

        /// <summary>
        /// Applies a multiview layout built at runtime (see <see cref="NhdDynamicMultiviewLayoutCalculator"/>)
        /// rather than from a named preset/custom layout config entry. Each tile's
        /// <see cref="NhdMultiviewTileState.SourceReference"/> is expected to be an Essentials device
        /// key, which is resolved here to the corresponding NetworkHD API alias for the wire command.
        /// </summary>
        public bool TryApplyDynamicLayout(IKeyed requestedBy, NhdBaseDevice rxEndpoint, IReadOnlyList<NhdMultiviewTileState> tiles)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to apply dynamic multiview layout: RX endpoint is null", source.Key);
                return false;
            }

            if (!rxEndpoint.SupportsMultiview)
            {
                Debug.LogError("[{0}] Endpoint '{1}' does not support dynamic multiview layout", source.Key, rxEndpoint.Key);
                return false;
            }

            if (!TryBuildDynamicLayoutCommand(rxEndpoint, tiles, out var command))
            {
                Debug.LogError("[{0}] Dynamic multiview layout for endpoint '{1}' has invalid geometry, no tiles, or an unresolvable source reference", source.Key, rxEndpoint.Key);
                return false;
            }

            var dynamicSent = NhdApiCommandSender.TrySend(source, command);
            if (dynamicSent)
            {
                // This is a runtime-computed layout, not a named preset/custom layout.
                rxEndpoint.SetActiveCustomMVLayout(null);
                RequestMultiviewState(rxEndpoint, source, force: true);
            }

            return dynamicSent;
        }

        public bool TryApplyMVPreset(IKeyed requestedBy, NhdBaseDevice rxEndpoint, string presetKey)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to apply multiview preset: RX endpoint is null", source.Key);
                return false;
            }

            if (!rxEndpoint.SupportsMultiview)
            {
                Debug.LogError("[{0}] Endpoint '{1}' does not support multiview preset apply", source.Key, rxEndpoint.Key);
                return false;
            }

            if (string.IsNullOrWhiteSpace(presetKey))
            {
                Debug.LogError("[{0}] Multiview preset key cannot be empty", source.Key);
                return false;
            }

            if (!rxEndpoint.TryGetMVPreset(presetKey, out var preset))
            {
                Debug.LogError("[{0}] Multiview preset '{1}' is not defined for endpoint '{2}'", source.Key, presetKey, rxEndpoint.Key);
                return false;
            }

            if (!TryValidateMultiviewPreset(source, rxEndpoint, preset, out var validationError))
            {
                Debug.LogError("[{0}] Multiview preset '{1}' is invalid for endpoint '{2}': {3}", source.Key, preset.Key, rxEndpoint.Key, validationError);
                return false;
            }

            var explicitWindowSources = new Dictionary<int, string>();
            var txByWindow = new Dictionary<int, NhdBaseDevice>();
            foreach (var route in preset.WindowRoutes ?? new List<NhdMultiviewPresetWindowRouteProperties>())
            {
                if (route == null || route.WindowReference <= 0)
                    continue;

                if (string.IsNullOrWhiteSpace(route.TxKey))
                    continue;

                if (!TryResolveTransmitter(route.TxKey, out var txEndpoint))
                    return false;

                explicitWindowSources[route.WindowReference] = txEndpoint.ApiEndpointReference;
                txByWindow[route.WindowReference] = txEndpoint;
            }

            var layoutApplied = false;
            var normalizedLayout = preset.Layout.Trim();
            if (preset.LayoutSource == NhdMultiviewPresetLayoutSource.Config)
            {
                layoutApplied = TryApplyCustomMVLayoutWithSources(source, rxEndpoint, normalizedLayout, explicitWindowSources);
            }
            else
            {
                layoutApplied = TryApplyControllerLayoutWithWindowRoutes(source, rxEndpoint, normalizedLayout, explicitWindowSources);
            }

            if (!layoutApplied)
                return false;

            if (!TryApplyPresetAudioSelection(source, rxEndpoint, preset, explicitWindowSources, txByWindow))
                return false;

            RequestMultiviewState(rxEndpoint, source, force: true);
            return true;

            bool TryResolveTransmitter(string txKey, out NhdBaseDevice txEndpoint)
            {
                txEndpoint = null;

                var normalizedTxKey = txKey?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedTxKey))
                    return false;

                txEndpoint = DeviceManager.GetDeviceForKey(normalizedTxKey) as NhdBaseDevice;
                if (txEndpoint == null || !txEndpoint.IsTransmitter)
                {
                    Debug.LogError("[{0}] Multiview preset '{1}' references unknown TX key '{2}'", source.Key, preset.Key, normalizedTxKey);
                    return false;
                }

                return true;
            }
        }

        public bool TryFullscreenMVTile(IKeyed requestedBy, NhdBaseDevice rxEndpoint, int sourceTileReference)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to fullscreen multiview tile: RX endpoint is null", source.Key);
                return false;
            }

            if (!rxEndpoint.SupportsMultiview)
            {
                Debug.LogError("[{0}] Endpoint '{1}' does not support multiview", source.Key, rxEndpoint.Key);
                return false;
            }

            if (sourceTileReference <= 0)
            {
                Debug.LogError("[{0}] Source tile reference must be >= 1", source.Key);
                return false;
            }

            if (!rxEndpoint.IsMVStateFresh(MultiviewStateFreshness))
            {
                _pendingFullscreenRequests[rxEndpoint.Key] = new PendingFullscreenRequest
                {
                    SourceTileReference = sourceTileReference,
                    RequestedByKey = source.Key,
                    QueuedUtc = DateTime.UtcNow,
                };

                RequestMultiviewState(rxEndpoint, source, force: true);
                return false;
            }

            if (rxEndpoint.ActiveTileCount <= 1)
            {
                Debug.LogError("[{0}] Fullscreen requires active multiview tile count > 1 on endpoint '{1}'", source.Key, rxEndpoint.Key);
                return false;
            }

            if (sourceTileReference > rxEndpoint.ActiveTileCount)
            {
                Debug.LogError("[{0}] Source tile {1} exceeds active tile count {2} on endpoint '{3}'", source.Key, sourceTileReference, rxEndpoint.ActiveTileCount, rxEndpoint.Key);
                return false;
            }

            var previousLayout = rxEndpoint.ActivePresetMultiviewLayoutName;
            if (string.IsNullOrWhiteSpace(previousLayout))
            {
                Debug.LogError("[{0}] Cannot fullscreen tile on endpoint '{1}' because active layout is unknown", source.Key, rxEndpoint.Key);
                return false;
            }

            if (!rxEndpoint.TryGetActiveMVTile(sourceTileReference, out var sourceTile) || string.IsNullOrWhiteSpace(sourceTile.SourceReference))
            {
                Debug.LogError("[{0}] Cannot fullscreen tile {1} on endpoint '{2}' because source is unknown", source.Key, sourceTileReference, rxEndpoint.Key);
                return false;
            }

            const string fullscreenLayout = "1-1";
            if (rxEndpoint.AvailablePresetMultiviewLayouts.Count > 0 && !rxEndpoint.IsKnownPresetMVLayout(fullscreenLayout))
            {
                Debug.LogError("[{0}] Fullscreen layout '{1}' is not available on endpoint '{2}'", source.Key, fullscreenLayout, rxEndpoint.Key);
                return false;
            }

            ClearFullscreenReturnState(rxEndpoint, "new fullscreen requested");

            _pendingFullscreen[rxEndpoint.Key] = new PendingMultiviewFullscreen
            {
                PreviousLayoutName = previousLayout,
                SourceTileReference = sourceTileReference,
                SourceReference = sourceTile.SourceReference,
                QueuedUtc = DateTime.UtcNow,
            };

            return NhdApiCommandSender.TrySend(source, $"mscene active {rxEndpoint.ApiEndpointReference} {fullscreenLayout}");
        }

        public bool TryReturnFromMVFullscreen(IKeyed requestedBy, NhdBaseDevice rxEndpoint)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to return from fullscreen: RX endpoint is null", source.Key);
                return false;
            }

            if (!TryGetMVFullscreenReturnLayout(rxEndpoint, out var returnLayout))
            {
                Debug.LogError("[{0}] No fullscreen return layout is available for endpoint '{1}'", source.Key, rxEndpoint.Key);
                return false;
            }

            _pendingFullscreenReturns[rxEndpoint.Key] = returnLayout;

            var sent = NhdApiCommandSender.TrySend(source, $"mscene active {rxEndpoint.ApiEndpointReference} {returnLayout}");
            if (!sent)
            {
                _pendingFullscreenReturns.Remove(rxEndpoint.Key);
            }

            return sent;
        }

        public bool TryGetMVFullscreenReturnLayout(NhdBaseDevice rxEndpoint, out string layoutName)
        {
            layoutName = null;

            if (rxEndpoint == null)
                return false;

            if (!_fullscreenReturnStates.TryGetValue(rxEndpoint.Key, out var state))
                return false;

            if (string.IsNullOrWhiteSpace(state.PreviousLayoutName))
                return false;

            layoutName = state.PreviousLayoutName;
            return true;
        }

        public bool TryProbeAndLearnMVLayouts(IKeyed requestedBy, NhdBaseDevice rxEndpoint)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to probe multiview layouts: RX endpoint is null", source.Key);
                return false;
            }

            if (!rxEndpoint.SupportsMultiview)
            {
                Debug.LogError("[{0}] Endpoint '{1}' does not support multiview preset layouts", source.Key, rxEndpoint.Key);
                return false;
            }

            if (_pendingLayoutProbes.ContainsKey(rxEndpoint.Key))
            {
                Debug.LogError("[{0}] Layout probe already running for endpoint '{1}'", source.Key, rxEndpoint.Key);
                return false;
            }

            if (rxEndpoint.AvailablePresetMultiviewLayouts.Count == 0)
            {
                Debug.LogError("[{0}] Cannot start layout probe for endpoint '{1}' because no preset layout list is known yet", source.Key, rxEndpoint.Key);
                RequestMultiviewPresetLayouts(rxEndpoint, source);
                return false;
            }

            var orderedLayouts = rxEndpoint.AvailablePresetMultiviewLayouts
                .OrderBy(layout => ParseLayoutTileCount(layout))
                .ThenBy(layout => layout, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var probe = new PendingLayoutProbe
            {
                EndpointKey = rxEndpoint.Key,
                RemainingLayouts = new Queue<string>(orderedLayouts),
                RequestedByKey = source.Key,
            };

            CaptureStartupProbeRestoreState(rxEndpoint);
            _pendingLayoutProbes[rxEndpoint.Key] = probe;

            if (!TrySendNextProbeLayout(rxEndpoint))
            {
                _pendingLayoutProbes.Remove(rxEndpoint.Key);
                _startupProbeRestoreStates.Remove(rxEndpoint.Key);
                return false;
            }

            return true;
        }

        public bool TryReprobeAndLearnMVLayouts(IKeyed requestedBy, NhdBaseDevice rxEndpoint)
        {
            var source = requestedBy ?? _ctl;

            if (rxEndpoint == null)
            {
                Debug.LogError("[{0}] Unable to reprobe multiview layouts: RX endpoint is null", source.Key);
                return false;
            }

            _pendingLayoutGeometryCapture.Remove(rxEndpoint.Key);
            _pendingLayoutProbes.Remove(rxEndpoint.Key);
            _startupProbeRestoreStates.Remove(rxEndpoint.Key);
            _startupProbeCompleted.Remove(rxEndpoint.Key);
            rxEndpoint.ClearLearnedPresetLayoutGeometrySignatures();

            return TryProbeAndLearnMVLayouts(source, rxEndpoint);
        }

        private void HandleLineReceived(object sender, GenericCommMethodReceiveTextArgs args)
        {
            ExpirePendingTileRoutes();
            ExpirePendingFullscreenRequests();

            var line = (args.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(line))
                return;

            if (TryHandleSessionLifecycleLine(line))
                return;

            if (TryHandleMultiviewInformationBlock(line))
                return;

            if (TryHandleMatrixInformationBlock(line))
                return;

            if (TryHandleMatrixSetResponseLine(line))
                return;

            if (TryHandleMsceneListBlock(line))
                return;

            if (TryHandleMsceneActiveResponseLine(line))
                return;

            if (TryHandleMsceneChangeResponseLine(line))
                return;

            if (TryHandleMsceneSetAudioResponseLine(line))
                return;

            if (TryHandleMviewSetAudioResponseLine(line))
                return;

            if (TryHandleAliasMappingLine(line))
                return;

            if (TryHandleDeviceStatusBlock(line))
                return;

            if (TryHandleEndpointNotifyLine(line))
                return;

            if (TryHandleSinkNotifyLine(line))
                return;

            if (TryHandleVideoNotifyLine(line))
                return;

            TryHandleDeviceListLine(line);
        }

        private void HandleRawTextReceived(object sender, GenericCommMethodReceiveTextArgs args)
        {
            var chunk = args?.Text;
            if (string.IsNullOrEmpty(chunk))
                return;

            TryHandleTelnetNegotiationBytes(chunk);
            TryHandleSessionLifecycleChunk(chunk);
        }

        private void TryHandleSessionLifecycleChunk(string chunk)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                return;

            if (chunk.IndexOf("*** IDLE TIMEOUT ***", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ArmBootstrap("idle timeout");
                return;
            }

            if (chunk.IndexOf("Unable to Login", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleLoginFailure("login failed");
                return;
            }

            if (TryHandleCredentialPrompts(chunk))
            {
                return;
            }

            if (!_isSessionReady && chunk.IndexOf("Welcome to NetworkHD", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MarkSessionReady("welcome banner");
            }
        }

        private void TryHandleTelnetNegotiationBytes(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
                return;

            var bytes = Encoding.GetEncoding(28591).GetBytes(chunk);
            if (bytes.Length == 0)
                return;

            var response = new List<byte>();
            for (var i = 0; i < bytes.Length; i++)
            {
                var value = bytes[i];

                if (_telnetAwaitingOption)
                {
                    AppendTelnetNegotiationReply(_telnetPendingCommand, value, response);
                    _telnetAwaitingOption = false;
                    continue;
                }

                if (_telnetAwaitingCommand)
                {
                    if (value == TelnetWill || value == TelnetWont || value == TelnetDo || value == TelnetDont)
                    {
                        _telnetPendingCommand = value;
                        _telnetAwaitingOption = true;
                    }

                    _telnetAwaitingCommand = false;
                    continue;
                }

                if (value == TelnetIac)
                {
                    _telnetAwaitingCommand = true;
                }
            }

            if (response.Count <= 0)
                return;

            _ctl.Comms.SendBytes(response.ToArray());
        }

        private static void AppendTelnetNegotiationReply(byte command, byte option, List<byte> response)
        {
            if (response == null)
                return;

            switch (command)
            {
                case TelnetWill:
                    response.Add(TelnetIac);
                    response.Add(SupportsRemoteWillOption(option) ? TelnetDo : TelnetDont);
                    response.Add(option);
                    break;

                case TelnetWont:
                    response.Add(TelnetIac);
                    response.Add(TelnetDont);
                    response.Add(option);
                    break;

                case TelnetDo:
                    response.Add(TelnetIac);
                    response.Add(SupportsLocalDoOption(option) ? TelnetWill : TelnetWont);
                    response.Add(option);
                    break;

                case TelnetDont:
                    response.Add(TelnetIac);
                    response.Add(TelnetWont);
                    response.Add(option);
                    break;
            }
        }

        private bool TryHandleSessionLifecycleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            if (line.IndexOf("*** IDLE TIMEOUT ***", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ArmBootstrap("idle timeout");
                return true;
            }

            if (line.StartsWith("Unable to Login", StringComparison.OrdinalIgnoreCase))
            {
                HandleLoginFailure("login failed");
                return true;
            }

            if (line.IndexOf("Welcome to NetworkHD", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MarkSessionReady("welcome banner");
                return true;
            }

            if (!_isSessionReady && IsLikelyApiResponseLine(line))
            {
                MarkSessionReady("api response");
                return false;
            }

            if (TryHandleTelnetNegotiationLine(line) && !_isSessionReady)
            {
                return true;
            }

            return false;
        }

        private bool TryHandleCredentialPrompts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var sawUserPrompt = ContainsStandalonePrompt(text, "User:");
            var sawPasswordPrompt = ContainsStandalonePrompt(text, "Password:");
            if (!sawUserPrompt && !sawPasswordPrompt)
                return false;

            var now = DateTime.UtcNow;
            var username = _ctl.ApiUsername ?? string.Empty;
            var password = _ctl.ApiPassword ?? string.Empty;

            if (sawUserPrompt
                && !IsDuplicatePrompt(ref _lastStandaloneUserPromptSeenUtc, now)
                && (!_lastUserPromptHandledUtc.HasValue || now - _lastUserPromptHandledUtc.Value >= CredentialPromptThrottle))
            {
                _lastUserPromptHandledUtc = now;
                ArmBootstrap("login prompt detected");
                SendUsernameCredential(username);
            }

            if (sawPasswordPrompt
                && !IsDuplicatePrompt(ref _lastStandalonePasswordPromptSeenUtc, now)
                && (!_lastPasswordPromptHandledUtc.HasValue || now - _lastPasswordPromptHandledUtc.Value >= CredentialPromptThrottle))
            {
                _lastPasswordPromptHandledUtc = now;
                ArmBootstrap("password prompt detected");
                SendPasswordCredential(password);
            }

            return true;
        }

        private static bool IsDuplicatePrompt(ref DateTime? lastSeenUtc, DateTime now)
        {
            if (lastSeenUtc.HasValue && now - lastSeenUtc.Value < PromptDedupWindow)
                return true;

            lastSeenUtc = now;
            return false;
        }

        private void SendUsernameCredential(string username)
        {
            _ctl.Comms.SendText((username ?? string.Empty) + "\r\n");
        }

        private void SendPasswordCredential(string password)
        {
            _ctl.Comms.SendText((password ?? string.Empty) + "\r\n");
        }

        private void HandleLoginFailure(string reason)
        {
            var now = DateTime.UtcNow;
            if (_lastLoginFailureHandledUtc.HasValue && now - _lastLoginFailureHandledUtc.Value < LoginFailureThrottle)
                return;

            _lastLoginFailureHandledUtc = now;
            ArmBootstrap(reason);

            Debug.LogError(
                "[{0}] CTL login failed. Verify configured API username/password.",
                _ctl.Key);
        }

        private static bool ContainsStandalonePrompt(string text, string promptToken)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(promptToken))
                return false;

            var searchIndex = 0;
            while (searchIndex < text.Length)
            {
                var index = text.IndexOf(promptToken, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    return false;

                var nextIndex = index + promptToken.Length;
                if (nextIndex >= text.Length)
                    return true;

                var next = text[nextIndex];
                if (next == '\r' || next == '\n' || char.IsWhiteSpace(next))
                    return true;

                searchIndex = nextIndex;
            }

            return false;
        }

        private bool HasRecentCredentialPrompt()
        {
            var now = DateTime.UtcNow;

            if (_lastUserPromptHandledUtc.HasValue && now - _lastUserPromptHandledUtc.Value < SessionProbeThrottle)
                return true;

            if (_lastPasswordPromptHandledUtc.HasValue && now - _lastPasswordPromptHandledUtc.Value < SessionProbeThrottle)
                return true;

            return false;
        }

        private static bool IsLikelyApiResponseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            if (TelnetNegotiationRegex.IsMatch(line))
                return false;

            if (line.StartsWith("User:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Password:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Unable to Login", StringComparison.OrdinalIgnoreCase)
                || line.IndexOf("*** IDLE TIMEOUT ***", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return line.StartsWith("config ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("notify ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("matrix ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("mview ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("mscene ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("+OK", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("-ERR", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryHandleTelnetNegotiationLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !TelnetNegotiationRegex.IsMatch(line))
                return false;

            var tokens = ParseTelnetHexTokens(line);
            if (tokens.Count < 3)
                return true;

            var response = new List<byte>();
            for (var i = 0; i + 2 < tokens.Count; i += 3)
            {
                if (tokens[i] != TelnetIac)
                    continue;

                var command = tokens[i + 1];
                var option = tokens[i + 2];

                AppendTelnetNegotiationReply(command, option, response);
            }

            if (response.Count > 0)
            {
                _ctl.Comms.SendBytes(response.ToArray());
            }

            return true;
        }

        private static List<byte> ParseTelnetHexTokens(string line)
        {
            var values = new List<byte>();
            if (string.IsNullOrWhiteSpace(line))
                return values;

            var matches = TelnetHexTokenRegex.Matches(line);
            foreach (Match match in matches)
            {
                if (!match.Success)
                    continue;

                var hex = match.Groups["hex"].Value;
                if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            return values;
        }

        private static bool SupportsRemoteWillOption(byte option)
        {
            return option == TelnetOptionEcho || option == TelnetOptionSuppressGoAhead;
        }

        private static bool SupportsLocalDoOption(byte option)
        {
            // This client only agrees to suppress-go-ahead locally.
            return option == TelnetOptionSuppressGoAhead;
        }

        private void ArmBootstrap(string reason)
        {
            var wasReady = _isSessionReady;

            _isSessionReady = false;
            _bootstrapPending = true;
            _isParsingMviewInformation = false;
            _isParsingMatrixInformation = false;
            _isParsingMsceneList = false;
            _pendingMviewEndpoint = null;
            _pendingMviewTiles.Clear();
            _subscribedNotificationReferences.Clear();
            _lastMatrixRefreshUtc = null;
            _startupProbeRestoreStates.Clear();

            if (wasReady)
            {
                OnSessionReadyStateChanged(false, reason);

                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Information,
                    "Session marked not-ready; reason='{Reason}'",
                    _ctl,
                    reason ?? "unspecified");
            }
        }

        private void MarkSessionReady(string reason)
        {
            var wasReady = _isSessionReady;
            _isSessionReady = true;

            if (!wasReady)
            {
                OnSessionReadyStateChanged(true, reason);
            }

            Debug.LogMessage(
                Serilog.Events.LogEventLevel.Information,
                "Session ready; reason='{Reason}', bootstrapPending={BootstrapPending}",
                _ctl,
                reason ?? "unspecified",
                _bootstrapPending);

            if (_bootstrapPending)
            {
                _bootstrapPending = false;
                RunBootstrapQueries();
            }
            else if (!wasReady)
            {
                RequestMatrixState(_ctl, force: true);
            }
        }

        private void OnSessionReadyStateChanged(bool isReady, string reason)
        {
            var handler = SessionReadyStateChanged;
            if (handler == null)
                return;

            handler(this, new NhdCtlSessionReadyStateChangedEventArgs(isReady, reason ?? "unspecified"));
        }

        private void RunBootstrapQueries()
        {
            // Preferred endpoint references are aliases, but some replies still return hostnames.
            _lastMsceneListRequestUtc.Clear();

            Debug.LogMessage(
                Serilog.Events.LogEventLevel.Information,
                "Running CTL bootstrap queries",
                _ctl);

            NhdApiCommandSender.TrySend(_ctl, "config set session alias on");
            NhdApiCommandSender.TrySend(_ctl, "config get name");
            NhdApiCommandSender.TrySend(_ctl, "config get devicelist");
            RequestDeviceStatus(_ctl, force: true);

            foreach (var endpoint in GetEndpoints())
            {
                EnsureNotificationsSubscribed(endpoint.ConfiguredAlias, _ctl);
            }

            RequestMatrixState(_ctl, force: true);
        }

        private void EnsureConnected()
        {
            if (_ctl.Comms == null)
                return;

            if (_ctl.Comms.IsConnected)
                return;
            _ctl.Comms.Connect();
        }

        private void SendSessionProbe(string reason)
        {
            if (_ctl.Comms == null)
                return;

            if (HasRecentCredentialPrompt())
                return;

            if (_lastSessionProbeUtc.HasValue && DateTime.UtcNow - _lastSessionProbeUtc.Value < SessionProbeThrottle)
                return;

            EnsureConnected();
            if (!_ctl.Comms.IsConnected)
                return;

            _lastSessionProbeUtc = DateTime.UtcNow;

            if (_ctl.Comms is GenericSshClient)
            {
                // SSH sessions can come up without a banner/prompt; send a safe command probe
                // so first response can mark session ready and release bootstrap.
                SendPreReadyApiCommand("config get name");
                return;
            }

            _ctl.Comms.SendText("\r\n");
        }

        private void StartPeriodicMatrixRefresh()
        {
            _periodicMatrixRefreshTimer?.Dispose();
            _periodicMatrixRefreshTimer = new Timer(
                HandlePeriodicMatrixRefresh,
                null,
                MatrixPeriodicRefreshInterval,
                MatrixPeriodicRefreshInterval);
        }

        private void HandlePeriodicMatrixRefresh(object state)
        {
            try
            {
                if (!_isSessionReady || _ctl?.Comms == null || !_ctl.Comms.IsConnected)
                    return;

                RequestMatrixState(_ctl);
            }
            catch
            {
                // Timer exceptions should not crash runtime threads.
            }
        }

        private void SendPreReadyApiCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || _ctl.Comms == null)
                return;

            var normalized = command.Trim();
            _ctl.Comms.SendText(normalized + "\r\n");
        }

        private bool TryHandleMsceneActiveResponseLine(string line)
        {
            var match = MsceneActiveResponseRegex.Match(line);
            if (!match.Success)
                return false;

            var reference = match.Groups["reference"].Value.Trim();
            var layout = match.Groups["layout"].Value.Trim();
            var success = match.Groups["result"].Value.Equals("success", StringComparison.OrdinalIgnoreCase);

            var endpoint = ResolveEndpoint(reference);
            if (endpoint == null)
            {
                if (!success)
                {
                    Debug.LogMessage(
                        Serilog.Events.LogEventLevel.Warning,
                        "mscene active response unresolved endpoint='{EndpointRef}', layout='{Layout}', result='{Result}'",
                        _ctl,
                        reference,
                        layout,
                        "failure");
                }

                return true;
            }

            var handledFullscreenTransition = false;
            if (_pendingFullscreen.TryGetValue(endpoint.Key, out var pendingFullscreen)
                && string.Equals(layout, "1-1", StringComparison.OrdinalIgnoreCase))
            {
                _pendingFullscreen.Remove(endpoint.Key);

                if (success)
                {
                    var command = BuildPresetTileRouteCommand(
                        pendingFullscreen.SourceReference,
                        endpoint.ApiEndpointReference,
                        "1-1",
                        1);

                    var routeSent = NhdApiCommandSender.TrySend(_ctl, command);
                    if (routeSent)
                    {
                        _recentFullscreenRoutes[endpoint.Key] = new RecentFullscreenRoute
                        {
                            TxReference = pendingFullscreen.SourceReference,
                            LayoutName = "1-1",
                            TileReference = 1,
                            RequestedUtc = DateTime.UtcNow,
                        };

                        SetFullscreenReturnState(endpoint, pendingFullscreen.PreviousLayoutName);
                    }
                    else
                    {
                        var rollbackSent = NhdApiCommandSender.TrySend(
                            _ctl,
                            $"mscene active {endpoint.ApiEndpointReference} {pendingFullscreen.PreviousLayoutName}");

                        Debug.LogMessage(
                            Serilog.Events.LogEventLevel.Warning,
                            "Fullscreen transition route failed; rollback to previous layout '{Layout}' on endpoint '{EndpointKey}' was {RollbackState}",
                            _ctl,
                            pendingFullscreen.PreviousLayoutName,
                            endpoint.Key,
                            rollbackSent ? "sent" : "not sent");

                        ClearFullscreenReturnState(endpoint, "fullscreen route command failed");
                    }
                }

                handledFullscreenTransition = true;
            }

            var handledFullscreenReturn = false;
            if (_pendingFullscreenReturns.TryGetValue(endpoint.Key, out var returnLayout)
                && string.Equals(returnLayout, layout, StringComparison.OrdinalIgnoreCase))
            {
                _pendingFullscreenReturns.Remove(endpoint.Key);
                if (success)
                {
                    ClearFullscreenReturnState(endpoint, "returned from fullscreen");
                }

                handledFullscreenReturn = true;
            }

            if (success)
            {
                endpoint.SetActivePresetMVLayout(layout, inferred: false);
                endpoint.ApplyPresetLayoutAudioSetting(layout);
                _pendingLayoutGeometryCapture[endpoint.Key] = layout;

                if (NhdBaseDevice.TryInferPresetLayoutShape(layout, out var inferredTileCount, out var inferredMode))
                {
                    endpoint.SetMVRuntimeState(inferredMode, inferredTileCount);
                }

                RequestMultiviewState(endpoint, force: true);
            }

            if (success && !handledFullscreenTransition && !handledFullscreenReturn)
            {
                ClearFullscreenReturnState(endpoint, "layout recalled");
            }

            if (_pendingLayoutProbes.TryGetValue(endpoint.Key, out var probe)
                && string.Equals(probe.ActiveLayout, layout, StringComparison.OrdinalIgnoreCase)
                && !success)
            {
                probe.AttemptedCount++;

                TrySendNextProbeLayout(endpoint);
            }

            if (!success)
            {
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "mscene active endpoint='{EndpointKey}', layout='{Layout}', result='failure'",
                    _ctl,
                    endpoint.Key,
                    layout);
            }

            return true;
        }

        private bool TryHandleMsceneChangeResponseLine(string line)
        {
            var match = MsceneChangeResponseRegex.Match(line);
            if (!match.Success)
                return false;

            var reference = match.Groups["reference"].Value.Trim();
            var layout = match.Groups["layout"].Value.Trim();
            var tile = match.Groups["tile"].Value.Trim();
            var source = match.Groups["source"].Value.Trim();
            var success = match.Groups["result"].Value.Equals("success", StringComparison.OrdinalIgnoreCase);

            var endpoint = ResolveEndpoint(reference);
            if (endpoint == null)
            {
                if (!success)
                {
                    Debug.LogMessage(
                        Serilog.Events.LogEventLevel.Warning,
                        "mscene change unresolved endpoint='{EndpointRef}', layout='{Layout}', tile='{Tile}', source='{Source}', result='failure'",
                        _ctl,
                        reference,
                        layout,
                        tile,
                        source);
                }

                return true;
            }

            if (success)
            {
                endpoint.SetActivePresetMVLayout(layout, inferred: false);
                endpoint.ApplyPresetLayoutAudioSetting(layout);
                _pendingLayoutGeometryCapture[endpoint.Key] = layout;

                if (NhdBaseDevice.TryInferPresetLayoutShape(layout, out var inferredTileCount, out var inferredMode))
                {
                    endpoint.SetMVRuntimeState(inferredMode, inferredTileCount);
                }

                RequestMultiviewState(endpoint, force: true);

                var shouldKeepFullscreenReturn = int.TryParse(tile, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileReference)
                    && ShouldBypassFullscreenReturnClearForRoute(endpoint, source, layout, tileReference);

                if (!shouldKeepFullscreenReturn)
                {
                    ClearFullscreenReturnState(endpoint, "layout tile changed");
                }
            }

            if (!success)
            {
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "mscene change endpoint='{EndpointKey}', layout='{Layout}', tile='{Tile}', source='{Source}', result='failure'",
                    _ctl,
                    endpoint.Key,
                    layout,
                    tile,
                    source);
            }

            return true;
        }

        private bool TryHandleMsceneSetAudioResponseLine(string line)
        {
            var match = MsceneSetAudioResponseRegex.Match(line);
            if (!match.Success)
                return false;

            var reference = match.Groups["reference"].Value.Trim();
            var layout = match.Groups["layout"].Value.Trim();
            var mode = match.Groups["mode"].Value.Trim();
            var target = match.Groups["target"].Value.Trim();
            var success = match.Groups["result"].Value.Equals("success", StringComparison.OrdinalIgnoreCase);

            var endpoint = ResolveEndpoint(reference);
            if (success && endpoint != null)
            {
                if (mode.Equals("window", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var audioWindow))
                        endpoint.SetPresetLayoutAudioWindow(layout, audioWindow);
                }
                else if (mode.Equals("separate", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint.SetPresetLayoutAudioSeparateSource(layout, target);
                }

                // mscene set audio writes saved preset configuration and is not live until layout activation.
                if (string.Equals(endpoint.ActivePresetMultiviewLayoutName, layout, StringComparison.OrdinalIgnoreCase))
                {
                    endpoint.ApplyPresetLayoutAudioSetting(layout);
                    RequestMultiviewState(endpoint, force: true);
                }
            }

            if (!success)
            {
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "mscene set audio reference='{Reference}', resolvedEndpoint='{ResolvedEndpoint}', layout='{Layout}', mode='{Mode}', target='{Target}', result='failure'",
                    _ctl,
                    reference,
                    endpoint?.Key ?? "unresolved",
                    layout,
                    mode,
                    target);
            }

            return true;
        }

        private bool TryHandleMviewSetAudioResponseLine(string line)
        {
            var match = MviewSetAudioResponseRegex.Match(line);
            if (!match.Success)
                return false;

            var reference = match.Groups["reference"].Value.Trim();
            var source = match.Groups["source"].Value.Trim();
            var success = match.Groups["result"].Value.Equals("success", StringComparison.OrdinalIgnoreCase);

            var endpoint = ResolveEndpoint(reference);
            if (success && endpoint != null)
            {
                endpoint.SetActiveMVAudioSeparateSource(source);
                RequestMultiviewState(endpoint, force: true);
            }

            if (!success)
            {
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "mview set audio reference='{Reference}', resolvedEndpoint='{ResolvedEndpoint}', source='{Source}', result='failure'",
                    _ctl,
                    reference,
                    endpoint?.Key ?? "unresolved",
                    source);
            }

            return true;
        }

        private bool TryHandleAliasMappingLine(string line)
        {
            var match = AliasLineRegex.Match(line);
            if (!match.Success)
                return false;

            var hostname = match.Groups["hostname"].Value.Trim();
            var aliasValue = match.Groups["alias"].Value.Trim();
            var alias = aliasValue.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : aliasValue;

            var endpoint = ResolveEndpoint(alias) ?? ResolveEndpoint(hostname);
            if (endpoint == null)
            {
                return true;
            }

            var previousHostname = endpoint.Hostname;

            endpoint.SetResolvedHostname(hostname);

            var hostnameChanged = !string.Equals(previousHostname, endpoint.Hostname, StringComparison.OrdinalIgnoreCase);

            EnsureNotificationsSubscribed(hostname);
            EnsureNotificationsSubscribed(alias);

            // Alias discovery should not be treated as an online-state signal.
            if (hostnameChanged)
            {
                RequestMultiviewState(endpoint);
                RequestMultiviewPresetLayouts(endpoint);
                RequestMatrixState();
                TryStartStartupProbeIfReady(endpoint);
            }

            return true;
        }

        private bool TryHandleEndpointNotifyLine(string line)
        {
            var match = EndpointNotifyRegex.Match(line);
            if (!match.Success)
                return false;

            var isOnline = match.Groups["state"].Value == "+";
            var reference = match.Groups["reference"].Value.Trim();

            var endpoint = ResolveEndpoint(reference);
            endpoint?.SetOnlineState(isOnline);

            EnsureNotificationsSubscribed(reference);

            if (isOnline && endpoint != null)
            {
                RequestMultiviewState(endpoint);
                RequestMultiviewPresetLayouts(endpoint);
                RequestMatrixState();
                RequestDeviceStatus();
                TryStartStartupProbeIfReady(endpoint);
            }

            return true;
        }

        private bool TryHandleVideoNotifyLine(string line)
        {
            var match = VideoNotifyRegex.Match(line);
            if (!match.Success)
                return false;

            var reference = match.Groups["reference"].Value.Trim();
            var stateToken = !string.IsNullOrWhiteSpace(match.Groups["state1"].Value)
                ? match.Groups["state1"].Value.Trim()
                : match.Groups["state2"].Value.Trim();

            EnsureNotificationsSubscribed(reference);

            if (!TryParseVideoNotifyState(stateToken, out var syncDetected))
            {
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Notify video state could not be parsed: reference='{Reference}', token='{Token}'",
                    _ctl,
                    reference,
                    stateToken);
                return true;
            }

            var endpoint = ResolveEndpoint(reference);

            if (endpoint == null)
            {
                return true;
            }

            if (!syncDetected)
            {
                ScheduleVideoLostDebounce(endpoint, reference);

                return true;
            }

            if (CancelPendingVideoLostDebounce(endpoint.Key))
            {
                return true;
            }

            endpoint.SetInputSyncState(true);

            return true;
        }

        private void ScheduleVideoLostDebounce(NhdBaseDevice endpoint, string reference)
        {
            if (endpoint == null)
                return;

            Timer existing = null;
            lock (_videoLostDebounceLock)
            {
                if (_pendingVideoLostDebounceTimers.TryGetValue(endpoint.Key, out existing))
                {
                    _pendingVideoLostDebounceTimers.Remove(endpoint.Key);
                }
            }

            existing?.Dispose();

            Timer timer = null;
            timer = new Timer(_ =>
            {
                var shouldApply = false;
                lock (_videoLostDebounceLock)
                {
                    if (_pendingVideoLostDebounceTimers.TryGetValue(endpoint.Key, out var pendingTimer)
                        && ReferenceEquals(pendingTimer, timer))
                    {
                        _pendingVideoLostDebounceTimers.Remove(endpoint.Key);
                        shouldApply = true;
                    }
                }

                timer?.Dispose();

                if (!shouldApply)
                    return;

                endpoint.SetInputSyncState(false);
            }, null, VideoLostNotifyDebounce, Timeout.InfiniteTimeSpan);

            lock (_videoLostDebounceLock)
            {
                _pendingVideoLostDebounceTimers[endpoint.Key] = timer;
            }
        }

        private bool CancelPendingVideoLostDebounce(string endpointKey)
        {
            if (string.IsNullOrWhiteSpace(endpointKey))
                return false;

            Timer timer = null;
            lock (_videoLostDebounceLock)
            {
                if (!_pendingVideoLostDebounceTimers.TryGetValue(endpointKey, out timer))
                    return false;

                _pendingVideoLostDebounceTimers.Remove(endpointKey);
            }

            timer?.Dispose();
            return true;
        }

        private bool TryHandleSinkNotifyLine(string line)
        {
            var match = SinkNotifyRegex.Match(line);
            if (!match.Success)
                return false;

            var reference = match.Groups["reference"].Value.Trim();
            var stateToken = match.Groups["state"].Value.Trim();

            EnsureNotificationsSubscribed(reference);

            if (!TryParseVideoNotifyState(stateToken, out var sinkDetected))
            {
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Notify sink state could not be parsed: reference='{Reference}', token='{Token}'",
                    _ctl,
                    reference,
                    stateToken);
                return true;
            }

            var endpoint = ResolveEndpoint(reference);
            if (endpoint?.IsTransmitter == true)
            {
                endpoint.SetInputSyncState(sinkDetected);
            }

            return true;
        }

        private bool TryHandleDeviceStatusBlock(string line)
        {
            if (!_isParsingDeviceStatus)
            {
                if (!line.StartsWith("devices status info:", StringComparison.OrdinalIgnoreCase))
                    return false;

                _isParsingDeviceStatus = true;
                _deviceStatusBraceDepth = 0;
                _pendingDeviceStatusEntry = null;
                _deviceStatusSeenEndpointKeys.Clear();
                return true;
            }

            return TryConsumeDeviceStatusLine(line);
        }

        private bool TryConsumeDeviceStatusLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return true;

            if (IsLikelyApiResponseLine(line))
                return false;

            var trimmed = line.Trim();

            var openCount = CountCharacter(trimmed, '{');
            for (var i = 0; i < openCount; i++)
            {
                if (_deviceStatusBraceDepth == 1 && _pendingDeviceStatusEntry == null)
                {
                    _pendingDeviceStatusEntry = new PendingDeviceStatusEntry();
                }

                _deviceStatusBraceDepth++;
            }

            var keyValueMatch = DeviceStatusKeyValueRegex.Match(trimmed);
            if (keyValueMatch.Success)
            {
                if (_pendingDeviceStatusEntry == null && _deviceStatusBraceDepth >= 2)
                {
                    _pendingDeviceStatusEntry = new PendingDeviceStatusEntry();
                }

                TryApplyDeviceStatusAttribute(
                    _pendingDeviceStatusEntry,
                    keyValueMatch.Groups["key"].Value,
                    keyValueMatch.Groups["value"].Success
                        ? keyValueMatch.Groups["value"].Value
                        : keyValueMatch.Groups["valueBare"].Value);
            }

            var closeCount = CountCharacter(trimmed, '}');
            for (var i = 0; i < closeCount; i++)
            {
                if (_deviceStatusBraceDepth == 2)
                {
                    FinalizePendingDeviceStatusEntry();
                    _pendingDeviceStatusEntry = null;
                }

                if (_deviceStatusBraceDepth > 0)
                {
                    _deviceStatusBraceDepth--;
                }
            }

            if (_deviceStatusBraceDepth <= 0 && closeCount > 0)
            {
                foreach (var endpoint in GetEndpoints().Where(e => !_deviceStatusSeenEndpointKeys.Contains(e.Key)))
                {
                    endpoint.SetOnlineState(false);
                }

                _isParsingDeviceStatus = false;
                _deviceStatusBraceDepth = 0;
                _pendingDeviceStatusEntry = null;
                _deviceStatusSeenEndpointKeys.Clear();
            }

            return true;
        }

        private static int CountCharacter(string value, char character)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            var count = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == character)
                    count++;
            }

            return count;
        }

        private static void TryApplyDeviceStatusAttribute(PendingDeviceStatusEntry entry, string key, string value)
        {
            if (entry == null || string.IsNullOrWhiteSpace(key))
                return;

            var normalizedKey = key.Trim().ToLowerInvariant();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            switch (normalizedKey)
            {
                case "aliasname":
                    entry.Alias = normalizedValue;
                    break;

                case "name":
                    entry.Name = normalizedValue;
                    break;

                case "hdmi out resolution":
                    entry.HdmiOutResolution = normalizedValue;
                    break;

                case "status":
                case "state":
                case "online":
                case "online status":
                case "onlinestatus":
                    if (TryParseDeviceOnlineState(normalizedValue, out var isOnline))
                    {
                        entry.IsOnline = isOnline;
                    }
                    break;
            }
        }

        private static bool TryParseDeviceOnlineState(string token, out bool isOnline)
        {
            isOnline = false;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var value = token.Trim();

            if (value.Equals("online", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                || value.Equals("connected", StringComparison.OrdinalIgnoreCase)
                || value.Equals("present", StringComparison.OrdinalIgnoreCase)
                || value.Equals("up", StringComparison.OrdinalIgnoreCase)
                || value.Equals("active", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value == "1")
            {
                isOnline = true;
                return true;
            }

            if (value.Equals("offline", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("disconnected", StringComparison.OrdinalIgnoreCase)
                || value.Equals("absent", StringComparison.OrdinalIgnoreCase)
                || value.Equals("down", StringComparison.OrdinalIgnoreCase)
                || value.Equals("inactive", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value == "0")
            {
                isOnline = false;
                return true;
            }

            return false;
        }

        private void FinalizePendingDeviceStatusEntry()
        {
            if (_pendingDeviceStatusEntry == null)
                return;

            NhdBaseDevice endpoint = null;

            if (!string.IsNullOrWhiteSpace(_pendingDeviceStatusEntry.Alias))
            {
                endpoint = ResolveEndpoint(_pendingDeviceStatusEntry.Alias);
            }

            if (endpoint == null && !string.IsNullOrWhiteSpace(_pendingDeviceStatusEntry.Name))
            {
                endpoint = ResolveEndpoint(_pendingDeviceStatusEntry.Name);
            }

            if (endpoint == null)
                return;

            _deviceStatusSeenEndpointKeys.Add(endpoint.Key);

            if (!string.IsNullOrWhiteSpace(_pendingDeviceStatusEntry.Name))
            {
                endpoint.SetResolvedHostname(_pendingDeviceStatusEntry.Name);
            }

            if (!endpoint.IsTransmitter && !string.IsNullOrWhiteSpace(_pendingDeviceStatusEntry.HdmiOutResolution))
            {
                endpoint.SetHdmiOutResolution(_pendingDeviceStatusEntry.HdmiOutResolution);
            }

            endpoint.SetOnlineState(_pendingDeviceStatusEntry.IsOnline ?? true);
        }

        private static bool TryParseVideoNotifyState(string token, out bool syncDetected)
        {
            syncDetected = false;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var value = token.Trim();
            if (value == "+"
                || value.Equals("found", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                || value.Equals("sync", StringComparison.OrdinalIgnoreCase)
                || value.Equals("present", StringComparison.OrdinalIgnoreCase)
                || value.Equals("online", StringComparison.OrdinalIgnoreCase)
                || value == "1")
            {
                syncDetected = true;
                return true;
            }

            if (value == "-"
                || value.Equals("lost", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("nosync", StringComparison.OrdinalIgnoreCase)
                || value.Equals("absent", StringComparison.OrdinalIgnoreCase)
                || value.Equals("offline", StringComparison.OrdinalIgnoreCase)
                || value == "0")
            {
                syncDetected = false;
                return true;
            }

            return false;
        }

        private bool TryHandleDeviceListLine(string line)
        {
            const string prefix = "devicelist is";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var refs = line.Substring(prefix.Length).Trim()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var reference in refs)
            {
                EnsureNotificationsSubscribed(reference);
            }

            // Device presence is determined from endpoint notify and device-status parsing,
            // because devicelist can include endpoints that are currently offline.
            RequestDeviceStatus();

            return true;
        }

        private bool TryHandleMultiviewInformationBlock(string line)
        {
            if (_isParsingMviewInformation)
            {
                if (TryConsumeMviewInformationLine(line))
                    return true;

                FinalizePendingMviewInformationEntry();
                _isParsingMviewInformation = false;
            }

            if (line.StartsWith("mview information:", StringComparison.OrdinalIgnoreCase))
            {
                _isParsingMviewInformation = true;
                FinalizePendingMviewInformationEntry();
                return true;
            }

            return false;
        }

        private bool TryHandleMatrixInformationBlock(string line)
        {
            if (_isParsingMatrixInformation)
            {
                if (TryConsumeMatrixInformationLine(line))
                    return true;

                _isParsingMatrixInformation = false;
            }

            var headerMatch = MatrixInformationHeaderRegex.Match(line);
            if (!headerMatch.Success)
                return false;

            _pendingMatrixSignalType = GetMatrixSignalType(headerMatch.Groups["domain"].Value);
            _isParsingMatrixInformation = true;
            return true;
        }

        private bool TryConsumeMatrixInformationLine(string line)
        {
            var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // Matrix information entries are strict "<txRef> <rxRef>" pairs.
            // Requiring exactly two tokens prevents this parser from swallowing
            // unrelated lines while a matrix block is open.
            if (tokens.Length != 2)
                return false;

            var txReference = tokens[0].Trim();
            var rxReference = tokens[1].Trim();

            TryApplyMatrixRoute(txReference, rxReference, _pendingMatrixSignalType);
            return true;
        }

        private bool TryHandleMatrixSetResponseLine(string line)
        {
            var match = MatrixSetResponseRegex.Match(line);
            if (!match.Success)
                return false;

            if (!match.Groups["result"].Value.Equals("success", StringComparison.OrdinalIgnoreCase))
                return true;

            var txReference = match.Groups["tx"].Value.Trim();
            var rxReference = match.Groups["rx"].Value.Trim();
            var signalType = GetMatrixSignalType(match.Groups["domain"].Value);

            TryApplyMatrixRoute(txReference, rxReference, signalType);
            return true;
        }

        private void TryApplyMatrixRoute(string txReference, string rxReference, eRoutingSignalType signalType)
        {
            var rxEndpoint = ResolveEndpoint(rxReference);
            if (rxEndpoint == null)
                return;

            var txEndpointKey = IsNullRouteReference(txReference)
                ? null
                : ResolveEndpoint(txReference)?.Key;

            if (!string.IsNullOrWhiteSpace(txReference) && !IsNullRouteReference(txReference) && string.IsNullOrWhiteSpace(txEndpointKey))
                return;

            if (!NhdGlobalRouter.Instance.TrySetTrackedMatrixRoute(txEndpointKey, rxEndpoint.Key, signalType))
                return;
        }

        private static eRoutingSignalType GetMatrixSignalType(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
                return eRoutingSignalType.AudioVideo;

            var normalized = domain.Trim();
            if (normalized.Equals("video", StringComparison.OrdinalIgnoreCase))
                return eRoutingSignalType.Video;

            if (normalized.Equals("audio", StringComparison.OrdinalIgnoreCase))
                return eRoutingSignalType.Audio;

            if (normalized.Equals("usb", StringComparison.OrdinalIgnoreCase))
                return NhdRoutingSignalTypes.UsbInput | NhdRoutingSignalTypes.UsbOutput | eRoutingSignalType.Usb;

            if (normalized.Equals("serial", StringComparison.OrdinalIgnoreCase))
                return NhdRoutingSignalTypes.Serial;

            if (normalized.Equals("infrared", StringComparison.OrdinalIgnoreCase))
                return NhdRoutingSignalTypes.Ir;

            return eRoutingSignalType.AudioVideo;
        }

        private bool TryHandleMsceneListBlock(string line)
        {
            if (_isParsingMsceneList)
            {
                if (TryConsumeMsceneListLine(line))
                    return true;

                _isParsingMsceneList = false;
            }

            if (line.StartsWith("mscene list:", StringComparison.OrdinalIgnoreCase))
            {
                _isParsingMsceneList = true;
                return true;
            }

            return false;
        }

        private bool TryConsumeMsceneListLine(string line)
        {
            var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                return false;

            // Stop list parsing when a command/response line starts (for example: "mscene active ...").
            if (tokens[0].Equals("mscene", StringComparison.OrdinalIgnoreCase))
                return false;

            var layoutTokens = tokens.Skip(1).ToList();
            if (!layoutTokens.Any(t => t.Contains("-")))
                return false;

            var endpoint = ResolveEndpoint(tokens[0]);
            if (endpoint == null || !endpoint.SupportsMultiview)
                return true;

            var layouts = layoutTokens
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            endpoint.SetAvailablePresetMVLayouts(layouts);
            TryStartStartupProbeIfReady(endpoint);

            return true;
        }

        private bool TryConsumeMviewInformationLine(string line)
        {
            var match = MviewInformationLineRegex.Match(line);
            if (match.Success)
            {
                FinalizePendingMviewInformationEntry();

                var reference = match.Groups["reference"].Value.Trim();
                var endpoint = ResolveEndpoint(reference);
                if (endpoint == null || !endpoint.SupportsMultiview)
                    return true;

                if (!TryParseMode(match.Groups["mode"].Value, out var mode))
                    return true;

                _pendingMviewEndpoint = endpoint;
                _pendingMviewMode = mode;
                _pendingMviewTiles.Clear();
                AppendTileDescriptors(match.Groups["tiles"].Value);

                // Endpoint-scoped "mview get <rx>" responses are typically a single entry.
                // Finalize as soon as enough tile descriptors are parsed for the active probe layout.
                if (ShouldFinalizePendingMviewEntry())
                {
                    FinalizePendingMviewInformationEntry();
                    _isParsingMviewInformation = false;
                }

                return true;
            }

            if (_pendingMviewEndpoint != null && AppendTileDescriptors(line))
            {
                if (ShouldFinalizePendingMviewEntry())
                {
                    FinalizePendingMviewInformationEntry();
                    _isParsingMviewInformation = false;
                }

                return true;
            }

            return false;
        }

        private bool ShouldFinalizePendingMviewEntry()
        {
            if (_pendingMviewEndpoint == null)
                return false;

            if (_pendingMviewTiles.Count == 0)
                return false;

            if (_pendingLayoutGeometryCapture.TryGetValue(_pendingMviewEndpoint.Key, out var recalledLayout)
                && NhdBaseDevice.TryInferPresetLayoutShape(recalledLayout, out var expectedTileCount, out _))
            {
                return _pendingMviewTiles.Count >= expectedTileCount;
            }

            return true;
        }

        private static bool TryParseMode(string modeText, out NhdMultiStreamMode mode)
        {
            if (modeText.Equals("overlay", StringComparison.OrdinalIgnoreCase))
            {
                mode = NhdMultiStreamMode.Overlay;
                return true;
            }

            mode = NhdMultiStreamMode.Tile;
            return modeText.Equals("tile", StringComparison.OrdinalIgnoreCase);
        }

        private bool AppendTileDescriptors(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var tokens = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var appended = false;

            foreach (var token in tokens)
            {
                if (!TryParseTileDescriptor(token, _pendingMviewTiles.Count + 1, out var tile))
                    continue;

                _pendingMviewTiles.Add(tile);
                appended = true;
            }

            return appended;
        }

        private void FinalizePendingMviewInformationEntry()
        {
            if (_pendingMviewEndpoint == null)
                return;

            // Tile descriptors parsed from hardware feedback (see TryParseTileDescriptor) carry the
            // raw NHD-CTL API reference (alias/hostname) reported by the device, not the Essentials
            // device key - unlike tiles produced by Nhd150Rx.ApplyDynamicLayout, whose SourceReference
            // is already an Essentials key. Resolve each tile's source to its Essentials device key
            // here so _activeMultiviewTiles (and therefore NhdBaseDevice.CurrentLayout) is always
            // keyed consistently, matching what Nhd150Rx.SyncTileSinksToLayout already assumes.
            var resolvedTiles = _pendingMviewTiles
                .Select(t =>
                {
                    if (string.IsNullOrWhiteSpace(t.SourceReference))
                        return t;

                    var resolvedKey = ResolveEndpoint(t.SourceReference)?.Key;
                    return t.WithSourceReference(resolvedKey);
                })
                .ToList();

            _pendingMviewEndpoint.SetMVRuntimeState(_pendingMviewMode, resolvedTiles);
            CaptureOrInferActiveLayout(_pendingMviewEndpoint);
            TryDispatchPendingCustomWindowAudioForEndpoint(_pendingMviewEndpoint);
            TryDispatchPendingTileRouteForEndpoint(_pendingMviewEndpoint);
            TryDispatchPendingFullscreenRequestForEndpoint(_pendingMviewEndpoint);
            TryStartStartupProbeIfReady(_pendingMviewEndpoint);

            _pendingMviewEndpoint = null;
            _pendingMviewTiles.Clear();
        }

        private void CaptureOrInferActiveLayout(NhdBaseDevice endpoint)
        {
            if (endpoint == null || !endpoint.SupportsMultiview)
                return;

            if (_pendingLayoutGeometryCapture.TryGetValue(endpoint.Key, out var recalledLayout))
            {
                _pendingLayoutGeometryCapture.Remove(endpoint.Key);

                var captured = false;

                if (endpoint.TryCaptureActiveLayoutGeometry(recalledLayout))
                {
                    captured = true;
                    TryMatchStartupProbeOriginalLayout(endpoint, recalledLayout);
                }

                if (_pendingLayoutProbes.TryGetValue(endpoint.Key, out var probe)
                    && string.Equals(probe.ActiveLayout, recalledLayout, StringComparison.OrdinalIgnoreCase))
                {
                    probe.AttemptedCount++;
                    if (captured)
                        probe.LearnedCount++;

                    TrySendNextProbeLayout(endpoint);
                }

                return;
            }

            if (endpoint.TryIdentifyPresetLayoutByActiveGeometry(out var inferredLayout))
            {
                endpoint.SetActivePresetMVLayout(inferredLayout, inferred: true);
            }

            if (endpoint.TryIdentifyCustomLayoutByActiveGeometry(out var inferredCustomLayout))
            {
                endpoint.SetActiveCustomMVLayout(inferredCustomLayout, inferred: true);
            }
        }

        private static bool TryParseTileDescriptor(string token, int tileNumber, out NhdMultiviewTileState tile)
        {
            tile = null;

            var match = TileDescriptorRegex.Match(token);
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups["x"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x))
                return false;

            if (!int.TryParse(match.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                return false;

            if (!int.TryParse(match.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
                return false;

            if (!int.TryParse(match.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                return false;

            tile = new NhdMultiviewTileState(
                tileNumber,
                match.Groups["source"].Value,
                x,
                y,
                w,
                h,
                match.Groups["scale"].Value);

            return true;
        }

        private static bool CanRouteTileNow(NhdBaseDevice rxEndpoint, int tileReference)
        {
            if (!rxEndpoint.IsMVStateFresh(MultiviewStateFreshness))
                return false;

            if (rxEndpoint.ActiveTileCount <= 0)
                return false;

            return tileReference <= rxEndpoint.ActiveTileCount;
        }

        private static bool TryBuildScaledCustomLayoutCommand(
            NhdBaseDevice rxEndpoint,
            NhdCustomMultiviewLayoutProperties layout,
            IDictionary<int, string> sourceReferencesByWindow,
            out string command,
            out int outputWidth,
            out int outputHeight,
            out bool usedQueriedResolution)
        {
            command = null;
            outputWidth = 0;
            outputHeight = 0;
            usedQueriedResolution = false;

            if (rxEndpoint == null || layout == null)
                return false;

            var windows = (layout.Windows ?? new List<NhdCustomMultiviewWindowProperties>())
                .Where(w => w != null && w.WindowReference > 0)
                .OrderBy(w => w.WindowReference)
                .ToList();

            if (windows.Count == 0)
                return false;

            var canvasWidth = layout.CanvasWidth > 0 ? layout.CanvasWidth : 1920;
            var canvasHeight = layout.CanvasHeight > 0 ? layout.CanvasHeight : 1080;

            if (rxEndpoint.TryGetHdmiOutResolutionDimensions(out var queriedWidth, out var queriedHeight)
                && queriedWidth > 0
                && queriedHeight > 0)
            {
                outputWidth = queriedWidth;
                outputHeight = queriedHeight;
                usedQueriedResolution = true;
            }
            else
            {
                outputWidth = canvasWidth;
                outputHeight = canvasHeight;
            }

            var descriptors = new List<string>();
            foreach (var window in windows)
            {
                if (window.Width <= 0 || window.Height <= 0)
                    return false;

                var scaledX = ScaleCoordinate(window.X, canvasWidth, outputWidth);
                var scaledY = ScaleCoordinate(window.Y, canvasHeight, outputHeight);
                var scaledWidth = ScaleLength(window.Width, canvasWidth, outputWidth);
                var scaledHeight = ScaleLength(window.Height, canvasHeight, outputHeight);

                if (scaledX >= outputWidth)
                    scaledX = Math.Max(0, outputWidth - 1);

                if (scaledY >= outputHeight)
                    scaledY = Math.Max(0, outputHeight - 1);

                if (scaledX + scaledWidth > outputWidth)
                    scaledWidth = Math.Max(1, outputWidth - scaledX);

                if (scaledY + scaledHeight > outputHeight)
                    scaledHeight = Math.Max(1, outputHeight - scaledY);

                var sourceReference = "NULL";
                if (sourceReferencesByWindow != null
                    && sourceReferencesByWindow.TryGetValue(window.WindowReference, out var mappedSource)
                    && !string.IsNullOrWhiteSpace(mappedSource))
                {
                    sourceReference = mappedSource.Trim();
                }

                var scaleMode = window.Scale == NhdMultiviewScaleMode.Fit ? "fit" : "stretch";
                var descriptor = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}_{2}_{3}_{4}:{5}",
                    sourceReference,
                    scaledX,
                    scaledY,
                    scaledWidth,
                    scaledHeight,
                    scaleMode);

                if (window.Rotation.HasValue)
                {
                    descriptor = string.Format(CultureInfo.InvariantCulture, "{0}:{1}", descriptor, window.Rotation.Value);
                }

                descriptors.Add(descriptor);
            }

            var modeToken = layout.Mode == NhdMultiStreamMode.Overlay ? "overlay" : "tile";
            command = string.Format(
                CultureInfo.InvariantCulture,
                "mview set {0} {1} {2}",
                rxEndpoint.ApiEndpointReference,
                modeToken,
                string.Join(" ", descriptors));

            return true;
        }

        /// <summary>
        /// Builds an 'mview set' command directly from already-computed tile geometry (see
        /// <see cref="NhdDynamicMultiviewLayoutCalculator"/>), resolving each tile's source
        /// reference (an Essentials device key) to its NetworkHD API alias. Unlike
        /// <see cref="TryBuildScaledCustomLayoutCommand"/>, this does not scale against a configured
        /// canvas size - the caller is expected to have already computed geometry in actual output
        /// pixel space.
        /// </summary>
        // Resolves a tile's source device key to the transmitter endpoint that carries it. Accepts a
        // TX endpoint key directly, or a content source (e.g. camera, codec) tied to a TX via config
        // tie-lines, including through switching devices ahead of the TX (see NhdSourcePath).
        private static NhdBaseDevice ResolveTransmitterForSourceKey(string sourceKey)
        {
            return NhdSourcePath.FindTransmitter(sourceKey);
        }

        private static bool TryBuildDynamicLayoutCommand(NhdBaseDevice rxEndpoint, IReadOnlyList<NhdMultiviewTileState> tiles, out string command)
        {
            command = null;

            if (rxEndpoint == null)
                return false;

            // No visible tiles (e.g. every participant camera muted with no presentation active). A
            // "matrix set null" does not apply while an RX is in multiview mode, so blank the canvas
            // with a single full-canvas null tile. Returning false here instead would leave the decoder
            // showing its previous layout (the last un-muted camera). Use the decoder's actual output
            // resolution so the null tile covers the whole canvas on 4K endpoints (not just 1080p).
            // No visible tiles (e.g. every participant camera muted with no presentation active). A
            // "matrix set null" does not apply while an RX is in multiview mode, so blank the canvas
            // with a single full-canvas null tile. Returning false here instead would leave the decoder
            // showing its previous layout (the last un-muted camera). The NHD multiview coordinate space
            // is the fixed 1920x1080 reference canvas (see NhdDynamicMultiviewLayoutCalculator), not the
            // decoder's HDMI-out resolution, so the null tile must use that reference size.
            if (tiles == null || tiles.Count == 0)
            {
                command = string.Format(
                    CultureInfo.InvariantCulture,
                    "mview set {0} tile null:0_0_{1}_{2}:fit",
                    rxEndpoint.ApiEndpointReference,
                    NhdDynamicMultiviewLayoutCalculator.DefaultCanvasWidth,
                    NhdDynamicMultiviewLayoutCalculator.DefaultCanvasHeight);
                return true;
            }

            var descriptors = new List<string>();
            foreach (var tile in tiles.OrderBy(t => t.TileNumber))
            {
                if (tile == null || tile.Width <= 0 || tile.Height <= 0)
                    return false;

                var sourceReference = "NULL";
                if (!string.IsNullOrWhiteSpace(tile.SourceReference))
                {
                    var txEndpoint = ResolveTransmitterForSourceKey(tile.SourceReference);
                    if (txEndpoint == null)
                        return false;

                    sourceReference = txEndpoint.ApiEndpointReference;
                }

                var scaleMode = string.Equals(tile.ScaleMode, NhdMultiviewScaleMode.Stretch.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? "stretch"
                    : "fit";

                descriptors.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}_{2}_{3}_{4}:{5}",
                    sourceReference,
                    tile.X,
                    tile.Y,
                    tile.Width,
                    tile.Height,
                    scaleMode));
            }

            command = string.Format(
                CultureInfo.InvariantCulture,
                "mview set {0} tile {1}",
                rxEndpoint.ApiEndpointReference,
                string.Join(" ", descriptors));

            return true;
        }

        private bool TryValidateMultiviewPreset(
            IKeyed source,
            NhdBaseDevice rxEndpoint,
            NhdMultiviewPresetProperties preset,
            out string validationError)
        {
            validationError = null;

            if (source == null || rxEndpoint == null || preset == null)
            {
                validationError = "source/endpoint/preset is null";
                return false;
            }

            if (string.IsNullOrWhiteSpace(preset.Layout))
            {
                validationError = "layout is required";
                return false;
            }

            var normalizedLayout = preset.Layout.Trim();

            if (preset.LayoutSource == NhdMultiviewPresetLayoutSource.Config)
            {
                if (!rxEndpoint.TryGetCustomMVLayout(normalizedLayout, out _))
                {
                    validationError = string.Format(CultureInfo.InvariantCulture, "config layout '{0}' is not defined in CustomMultiviewLayouts", normalizedLayout);
                    return false;
                }
            }
            else
            {
                var knownByController = rxEndpoint.AvailablePresetMultiviewLayouts.Contains(normalizedLayout)
                    || NhdBaseDevice.TryInferPresetLayoutShape(normalizedLayout, out _, out _);

                if (!knownByController)
                {
                    validationError = string.Format(CultureInfo.InvariantCulture, "controller layout '{0}' is not known", normalizedLayout);
                    return false;
                }
            }

            var duplicateWindow = (preset.WindowRoutes ?? new List<NhdMultiviewPresetWindowRouteProperties>())
                .Where(r => r != null && r.WindowReference > 0)
                .GroupBy(r => r.WindowReference)
                .FirstOrDefault(g => g.Count() > 1);

            if (duplicateWindow != null)
            {
                validationError = string.Format(CultureInfo.InvariantCulture, "duplicate WindowReference '{0}' in WindowRoutes", duplicateWindow.Key);
                return false;
            }

            var invalidWindow = (preset.WindowRoutes ?? new List<NhdMultiviewPresetWindowRouteProperties>())
                .FirstOrDefault(r => r != null && r.WindowReference <= 0);
            if (invalidWindow != null)
            {
                validationError = "WindowRoutes contains WindowReference <= 0";
                return false;
            }

            foreach (var route in preset.WindowRoutes ?? new List<NhdMultiviewPresetWindowRouteProperties>())
            {
                if (route == null || string.IsNullOrWhiteSpace(route.TxKey))
                    continue;

                var tx = DeviceManager.GetDeviceForKey(route.TxKey.Trim()) as NhdBaseDevice;
                if (tx == null || !tx.IsTransmitter)
                {
                    validationError = string.Format(CultureInfo.InvariantCulture, "WindowReference '{0}' references unknown TX key '{1}'", route.WindowReference, route.TxKey);
                    return false;
                }
            }

            if (preset.AudioMode.HasValue)
            {
                if (preset.AudioMode.Value == NhdMultiviewAudioMode.NoChange
                    || preset.AudioMode.Value == NhdMultiviewAudioMode.Unknown)
                {
                    return true;
                }

                if (preset.AudioMode.Value == NhdMultiviewAudioMode.Window)
                {
                    if (!preset.AudioWindowReference.HasValue || preset.AudioWindowReference.Value <= 0)
                    {
                        validationError = "AudioMode 'Window' requires AudioWindowReference >= 1";
                        return false;
                    }

                    if (preset.AudioWindowReference.Value > rxEndpoint.MaxStreamCount)
                    {
                        validationError = string.Format(
                            CultureInfo.InvariantCulture,
                            "AudioWindowReference '{0}' exceeds endpoint MaxStreamCount '{1}'",
                            preset.AudioWindowReference.Value,
                            rxEndpoint.MaxStreamCount);
                        return false;
                    }
                }
                else if (preset.AudioMode.Value == NhdMultiviewAudioMode.Separate)
                {
                    if (string.IsNullOrWhiteSpace(preset.AudioTxKey))
                    {
                        validationError = "AudioMode 'Separate' requires AudioTxKey";
                        return false;
                    }

                    var audioTx = DeviceManager.GetDeviceForKey(preset.AudioTxKey.Trim()) as NhdBaseDevice;
                    if (audioTx == null || !audioTx.IsTransmitter)
                    {
                        validationError = string.Format(CultureInfo.InvariantCulture, "AudioTxKey '{0}' is not a known transmitter", preset.AudioTxKey);
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryApplyControllerLayoutWithWindowRoutes(
            IKeyed source,
            NhdBaseDevice rxEndpoint,
            string layoutName,
            IReadOnlyDictionary<int, string> sourceReferencesByWindow)
        {
            if (!TryActivateMVLayout(source, rxEndpoint, layoutName))
                return false;

            var tileCount = 0;
            if (!NhdBaseDevice.TryInferPresetLayoutShape(layoutName, out tileCount, out _))
            {
                tileCount = rxEndpoint.ActiveTileCount;
            }

            if (tileCount <= 0)
            {
                tileCount = sourceReferencesByWindow?.Count > 0
                    ? sourceReferencesByWindow.Keys.Max()
                    : 0;
            }

            if (tileCount <= 0)
                return true;

            var allSent = true;
            for (var tile = 1; tile <= tileCount; tile++)
            {
                var txReference = "null";
                if (sourceReferencesByWindow != null
                    && sourceReferencesByWindow.TryGetValue(tile, out var mappedSource)
                    && !string.IsNullOrWhiteSpace(mappedSource))
                {
                    txReference = mappedSource.Trim();
                }

                var routeCommand = BuildPresetTileRouteCommand(txReference, rxEndpoint.ApiEndpointReference, layoutName, tile);
                if (!NhdApiCommandSender.TrySend(source, routeCommand))
                {
                    allSent = false;
                }
            }

            return allSent;
        }

        private bool TryApplyPresetAudioSelection(
            IKeyed source,
            NhdBaseDevice rxEndpoint,
            NhdMultiviewPresetProperties preset,
            IReadOnlyDictionary<int, string> sourceReferencesByWindow,
            IReadOnlyDictionary<int, NhdBaseDevice> txByWindow)
        {
            if (source == null || rxEndpoint == null || preset == null || !preset.AudioMode.HasValue)
                return true;

            if (preset.AudioMode.Value == NhdMultiviewAudioMode.NoChange
                || preset.AudioMode.Value == NhdMultiviewAudioMode.Unknown)
            {
                return true;
            }

            if (preset.AudioMode.Value == NhdMultiviewAudioMode.Window)
            {
                var audioWindow = preset.AudioWindowReference.Value;
                rxEndpoint.SetActiveMVAudioWindow(audioWindow);

                string windowSourceRef = null;
                if (sourceReferencesByWindow != null
                    && sourceReferencesByWindow.TryGetValue(audioWindow, out var mappedWindowSource)
                    && !string.IsNullOrWhiteSpace(mappedWindowSource))
                {
                    windowSourceRef = mappedWindowSource.Trim();
                }
                else if (rxEndpoint.TryGetActiveMVTile(audioWindow, out var tile)
                    && tile != null
                    && !string.IsNullOrWhiteSpace(tile.SourceReference))
                {
                    windowSourceRef = tile.SourceReference.Trim();
                }

                if (!string.IsNullOrWhiteSpace(windowSourceRef))
                {
                    var audioCommand = string.Format(
                        CultureInfo.InvariantCulture,
                        "mview set audio {0} separate {1}",
                        rxEndpoint.ApiEndpointReference,
                        windowSourceRef);

                    if (!NhdApiCommandSender.TrySend(source, audioCommand))
                        return false;

                    rxEndpoint.SetActiveMVAudioSeparateSource(windowSourceRef);
                }

                return true;
            }

            if (preset.AudioMode.Value == NhdMultiviewAudioMode.Separate)
            {
                var audioTx = DeviceManager.GetDeviceForKey(preset.AudioTxKey.Trim()) as NhdBaseDevice;
                if (audioTx == null || !audioTx.IsTransmitter)
                    return false;

                var audioSourceRef = audioTx.ApiEndpointReference;
                var command = string.Format(
                    CultureInfo.InvariantCulture,
                    "mview set audio {0} separate {1}",
                    rxEndpoint.ApiEndpointReference,
                    audioSourceRef);

                if (!NhdApiCommandSender.TrySend(source, command))
                    return false;

                rxEndpoint.SetActiveMVAudioSeparateSource(audioSourceRef);
                return true;
            }

            return true;
        }

        private static bool TryValidateCustomLayoutAudioMetadata(
            NhdBaseDevice rxEndpoint,
            NhdCustomMultiviewLayoutProperties layout,
            out string validationError)
        {
            validationError = null;

            if (rxEndpoint == null || layout == null)
            {
                validationError = "endpoint/layout is null";
                return false;
            }

            if (!layout.AudioMode.HasValue || layout.AudioMode.Value == NhdMultiviewAudioMode.Unknown)
                return true;

            if (layout.AudioMode.Value == NhdMultiviewAudioMode.Window)
            {
                if (!layout.AudioWindowReference.HasValue || layout.AudioWindowReference.Value <= 0)
                {
                    validationError = "AudioMode 'Window' requires AudioWindowReference >= 1";
                    return false;
                }

                if (layout.AudioWindowReference.Value > rxEndpoint.MaxStreamCount)
                {
                    validationError = string.Format(
                        CultureInfo.InvariantCulture,
                        "AudioWindowReference '{0}' exceeds endpoint MaxStreamCount '{1}'",
                        layout.AudioWindowReference.Value,
                        rxEndpoint.MaxStreamCount);
                    return false;
                }
            }

            return true;
        }

        private bool TryApplyCustomLayoutAudioMetadata(
            IKeyed source,
            NhdBaseDevice rxEndpoint,
            NhdCustomMultiviewLayoutProperties layout)
        {
            if (source == null || rxEndpoint == null || layout == null)
                return false;

            if (!layout.AudioMode.HasValue || layout.AudioMode.Value == NhdMultiviewAudioMode.Unknown)
            {
                _pendingCustomWindowAudioApplies.Remove(rxEndpoint.Key);
                return true;
            }

            if (layout.AudioMode.Value == NhdMultiviewAudioMode.Window)
            {
                var windowReference = layout.AudioWindowReference.Value;
                rxEndpoint.SetActiveMVAudioWindow(windowReference);
                _pendingCustomWindowAudioApplies[rxEndpoint.Key] = windowReference;

                TryDispatchPendingCustomWindowAudioForEndpoint(rxEndpoint);
                return true;
            }

            if (layout.AudioMode.Value == NhdMultiviewAudioMode.Separate)
            {
                _pendingCustomWindowAudioApplies.Remove(rxEndpoint.Key);

                var separateSource = string.IsNullOrWhiteSpace(rxEndpoint.ActiveMultiviewAudioSeparateSourceReference)
                    ? rxEndpoint.ActiveMultiviewAudioSourceReference
                    : rxEndpoint.ActiveMultiviewAudioSeparateSourceReference;

                rxEndpoint.SetActiveMVAudioSeparateSource(separateSource);

                if (string.IsNullOrWhiteSpace(separateSource))
                {
                    Debug.LogMessage(
                        Serilog.Events.LogEventLevel.Warning,
                        "[{SourceKey}] Custom layout audio mode 'separate' selected for endpoint '{1}' but no source is available yet",
                        source,
                        source.Key,
                        rxEndpoint.Key);
                    return true;
                }

                var command = string.Format(
                    CultureInfo.InvariantCulture,
                    "mview set audio {0} separate {1}",
                    rxEndpoint.ApiEndpointReference,
                    separateSource);

                if (!NhdApiCommandSender.TrySend(source, command))
                {
                    Debug.LogError("[{0}] Failed sending custom layout separate-audio command endpoint='{1}', source='{2}'", source.Key, rxEndpoint.Key, separateSource);
                    return false;
                }

                return true;
            }

            _pendingCustomWindowAudioApplies.Remove(rxEndpoint.Key);
            return true;
        }

        private void TryDispatchPendingCustomWindowAudioForEndpoint(NhdBaseDevice rxEndpoint)
        {
            if (rxEndpoint == null)
                return;

            if (!_pendingCustomWindowAudioApplies.TryGetValue(rxEndpoint.Key, out var windowReference))
                return;

            if (!rxEndpoint.TryGetActiveMVTile(windowReference, out var tile)
                || tile == null
                || string.IsNullOrWhiteSpace(tile.SourceReference))
            {
                return;
            }

            var sourceReference = tile.SourceReference.Trim();
            var command = string.Format(
                CultureInfo.InvariantCulture,
                "mview set audio {0} separate {1}",
                rxEndpoint.ApiEndpointReference,
                sourceReference);

            if (!NhdApiCommandSender.TrySend(_ctl, command))
                return;

            rxEndpoint.SetActiveMVAudioSeparateSource(sourceReference);
            _pendingCustomWindowAudioApplies.Remove(rxEndpoint.Key);
        }

        private static int ScaleCoordinate(int value, int sourceSpan, int targetSpan)
        {
            if (sourceSpan <= 0 || targetSpan <= 0)
                return 0;

            if (value <= 0)
                return 0;

            var scaled = (double)value * targetSpan / sourceSpan;
            return Math.Max(0, (int)Math.Round(scaled, MidpointRounding.AwayFromZero));
        }

        private static int ScaleLength(int value, int sourceSpan, int targetSpan)
        {
            if (sourceSpan <= 0 || targetSpan <= 0)
                return 1;

            if (value <= 0)
                return 1;

            var scaled = (double)value * targetSpan / sourceSpan;
            return Math.Max(1, (int)Math.Round(scaled, MidpointRounding.AwayFromZero));
        }

        private void RequestMultiviewState(NhdBaseDevice endpoint, IKeyed source = null, bool force = false)
        {
            if (endpoint == null || !endpoint.SupportsMultiview)
                return;

            if (!force && endpoint.IsMVStateFresh(MultiviewRefreshThrottle))
                return;

            var sender = source ?? _ctl;
            NhdApiCommandSender.TrySend(sender, $"mview get {endpoint.ApiEndpointReference}");
        }

        private void RequestDeviceStatus(IKeyed source = null, bool force = false)
        {
            if (!force && _lastDeviceStatusRefreshUtc.HasValue && DateTime.UtcNow - _lastDeviceStatusRefreshUtc.Value < DeviceStatusRefreshThrottle)
                return;

            _lastDeviceStatusRefreshUtc = DateTime.UtcNow;

            var sender = source ?? _ctl;
            NhdApiCommandSender.TrySend(sender, "config get device status");
        }

        private void RequestMatrixState(IKeyed source = null, bool force = false)
        {
            if (!force && _lastMatrixRefreshUtc.HasValue && DateTime.UtcNow - _lastMatrixRefreshUtc.Value < MatrixRefreshThrottle)
                return;

            _lastMatrixRefreshUtc = DateTime.UtcNow;

            var sender = source ?? _ctl;

            NhdApiCommandSender.TrySend(sender, "matrix video get");
            NhdApiCommandSender.TrySend(sender, "matrix audio get");
            NhdApiCommandSender.TrySend(sender, "matrix usb get");
            NhdApiCommandSender.TrySend(sender, "matrix serial get");
            NhdApiCommandSender.TrySend(sender, "matrix infrared get");
        }

        private void EnsureNotificationsSubscribed(string endpointReference, IKeyed source = null)
        {
            // NetworkHD endpoint notifications (e.g. "notify endpoint + source1") are unsolicited:
            // the controller emits them automatically over the open telnet session and there is no
            // subscribe command. Sending "notify endpoint <ref>" returns "unknown command" and, because
            // that reply never matches the expected response, it stalled the serial command queue for the
            // full response timeout on every endpoint. No outbound action is required here.
        }

        private bool TrySendNextProbeLayout(NhdBaseDevice endpoint)
        {
            if (endpoint == null)
                return false;

            if (!_pendingLayoutProbes.TryGetValue(endpoint.Key, out var probe))
                return false;

            if (probe.RemainingLayouts.Count == 0)
            {
                _pendingLayoutProbes.Remove(endpoint.Key);
                TryRestoreLayoutAfterProbe(endpoint);
                return true;
            }

            var nextLayout = probe.RemainingLayouts.Dequeue();
            probe.ActiveLayout = nextLayout;

            return NhdApiCommandSender.TrySend(_ctl, $"mscene active {endpoint.ApiEndpointReference} {nextLayout}");
        }

        private void SetFullscreenReturnState(NhdBaseDevice endpoint, string previousLayoutName)
        {
            if (endpoint == null || string.IsNullOrWhiteSpace(previousLayoutName))
                return;

            _fullscreenReturnStates[endpoint.Key] = new MultiviewFullscreenReturnState
            {
                PreviousLayoutName = previousLayoutName,
                CapturedUtc = DateTime.UtcNow,
            };
        }

        private void ClearFullscreenReturnState(NhdBaseDevice endpoint, string reason)
        {
            if (endpoint == null)
                return;

            _pendingFullscreen.Remove(endpoint.Key);
            _pendingFullscreenReturns.Remove(endpoint.Key);

            if (!_fullscreenReturnStates.Remove(endpoint.Key))
                return;
        }

        private void TryStartStartupProbeIfReady(NhdBaseDevice endpoint)
        {
            if (endpoint == null || !endpoint.SupportsMultiview || endpoint.IsTransmitter)
                return;

            if (_startupProbeCompleted.Contains(endpoint.Key))
                return;

            if (_pendingLayoutProbes.ContainsKey(endpoint.Key))
                return;

            if (!endpoint.OnlineState)
                return;

            if (endpoint.AvailablePresetMultiviewLayouts.Count == 0)
                return;

            if (!endpoint.IsMVStateFresh(MultiviewStateFreshness))
            {
                RequestMultiviewState(endpoint, force: true);
                return;
            }

            if (TryProbeAndLearnMVLayouts(_ctl, endpoint))
            {
                _startupProbeCompleted.Add(endpoint.Key);
            }
        }

        private static int ParseLayoutTileCount(string layoutName)
        {
            return NhdBaseDevice.TryInferPresetLayoutShape(layoutName, out var tileCount, out _)
                ? tileCount
                : int.MaxValue;
        }

        private void CaptureStartupProbeRestoreState(NhdBaseDevice endpoint)
        {
            if (endpoint == null)
                return;

            _startupProbeRestoreStates[endpoint.Key] = new StartupProbeRestoreState
            {
                OriginalLayoutName = string.IsNullOrWhiteSpace(endpoint.ActivePresetMultiviewLayoutName)
                    ? null
                    : endpoint.ActivePresetMultiviewLayoutName.Trim(),
                OriginalGeometrySignature = BuildActiveGeometrySignature(endpoint),
            };
        }

        private void TryMatchStartupProbeOriginalLayout(NhdBaseDevice endpoint, string capturedLayout)
        {
            if (endpoint == null || string.IsNullOrWhiteSpace(capturedLayout))
                return;

            if (!_startupProbeRestoreStates.TryGetValue(endpoint.Key, out var state))
                return;

            if (!string.IsNullOrWhiteSpace(state.MatchedOriginalLayoutName))
                return;

            if (string.IsNullOrWhiteSpace(state.OriginalGeometrySignature))
                return;

            var activeSignature = BuildActiveGeometrySignature(endpoint);
            if (string.IsNullOrWhiteSpace(activeSignature))
                return;

            if (!string.Equals(activeSignature, state.OriginalGeometrySignature, StringComparison.Ordinal))
                return;

            state.MatchedOriginalLayoutName = capturedLayout;
        }

        private void TryRestoreLayoutAfterProbe(NhdBaseDevice endpoint)
        {
            if (endpoint == null)
                return;

            if (!_startupProbeRestoreStates.TryGetValue(endpoint.Key, out var state))
                return;

            _startupProbeRestoreStates.Remove(endpoint.Key);

            var restoreLayout = !string.IsNullOrWhiteSpace(state.MatchedOriginalLayoutName)
                ? state.MatchedOriginalLayoutName
                : state.OriginalLayoutName;

            if (string.IsNullOrWhiteSpace(restoreLayout))
                return;

            if (string.Equals(endpoint.ActivePresetMultiviewLayoutName, restoreLayout, StringComparison.OrdinalIgnoreCase))
                return;

            NhdApiCommandSender.TrySend(_ctl, $"mscene active {endpoint.ApiEndpointReference} {restoreLayout}");
        }

        private static string BuildActiveGeometrySignature(NhdBaseDevice endpoint)
        {
            if (endpoint == null || endpoint.ActiveTileCount <= 0)
                return null;

            var parts = new List<string>(endpoint.ActiveTileCount);
            for (var i = 1; i <= endpoint.ActiveTileCount; i++)
            {
                if (!endpoint.TryGetActiveMVTile(i, out var tile) || tile == null)
                    return null;

                parts.Add(string.Format("{0}:{1}_{2}_{3}_{4}", tile.TileNumber, tile.X, tile.Y, tile.Width, tile.Height));
            }

            return parts.Count == 0
                ? null
                : string.Join("|", parts);
        }

        private void RequestMultiviewPresetLayouts(NhdBaseDevice endpoint, IKeyed source = null)
        {
            if (endpoint == null || !endpoint.SupportsMultiview)
                return;

            if (_lastMsceneListRequestUtc.TryGetValue(endpoint.Key, out var lastRequestUtc) && DateTime.UtcNow - lastRequestUtc < MsceneListRefreshThrottle)
                return;

            _lastMsceneListRequestUtc[endpoint.Key] = DateTime.UtcNow;

            var sender = source ?? _ctl;
            NhdApiCommandSender.TrySend(sender, $"mscene get {endpoint.ApiEndpointReference}");
        }

        /// <summary>
        /// Forces an immediate controller request for the endpoint's available multiview preset
        /// layouts (the layouts shown in the NetworkHD Multiview UI), bypassing the refresh throttle.
        /// The controller response is parsed asynchronously into the endpoint's available layout list.
        /// </summary>
        public void RequestMVLayoutList(NhdBaseDevice endpoint, IKeyed source = null)
        {
            if (endpoint == null || !endpoint.SupportsMultiview)
                return;

            _lastMsceneListRequestUtc.Remove(endpoint.Key);
            RequestMultiviewPresetLayouts(endpoint, source);
        }

        private static string BuildPresetTileRouteCommand(string txReference, string rxReference, string layoutName, int tileReference)
        {
            return $"mscene change {rxReference} {layoutName} {tileReference} {txReference}";
        }

        private void TryDispatchPendingTileRouteForEndpoint(NhdBaseDevice rxEndpoint)
        {
            if (rxEndpoint == null)
                return;

            if (!_pendingTileRoutes.TryGetValue(rxEndpoint.Key, out var pending))
                return;

            if (DateTime.UtcNow - pending.QueuedUtc > PendingTileRouteExpiry)
            {
                _pendingTileRoutes.Remove(rxEndpoint.Key);
                return;
            }

            if (!rxEndpoint.IsMVStateFresh(MultiviewStateFreshness))
                return;

            if (pending.TileReference > rxEndpoint.ActiveTileCount)
            {
                _pendingTileRoutes.Remove(rxEndpoint.Key);
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Rejected queued multiview tile route for endpoint '{EndpointKey}': requested tile={RequestedTile}, activeTiles={ActiveTiles}, mode='{Mode}'",
                    _ctl,
                    rxEndpoint.Key,
                    pending.TileReference,
                    rxEndpoint.ActiveTileCount,
                    rxEndpoint.MultiStreamMode);
                return;
            }

            var txEndpoint = DeviceManager.GetDeviceForKey(pending.TxEndpointKey) as NhdBaseDevice;
            if (txEndpoint == null)
            {
                _pendingTileRoutes.Remove(rxEndpoint.Key);
                Debug.LogError("[{0}] Queued multiview tile route dropped: TX endpoint '{1}' not found", _ctl.Key, pending.TxEndpointKey);
                return;
            }

            _pendingTileRoutes.Remove(rxEndpoint.Key);

            var command = BuildPresetTileRouteCommand(
                txEndpoint.ApiEndpointReference,
                rxEndpoint.ApiEndpointReference,
                pending.LayoutName,
                pending.TileReference);

            var sent = NhdApiCommandSender.TrySend(_ctl, command);
            if (sent)
            {
                if (!ShouldBypassFullscreenReturnClearForRoute(rxEndpoint, txEndpoint.ApiEndpointReference, pending.LayoutName, pending.TileReference))
                {
                    ClearFullscreenReturnState(rxEndpoint, "tile route changed");
                }
            }
        }

        private void TryDispatchPendingFullscreenRequestForEndpoint(NhdBaseDevice rxEndpoint)
        {
            if (rxEndpoint == null)
                return;

            if (!_pendingFullscreenRequests.TryGetValue(rxEndpoint.Key, out var pending))
                return;

            if (DateTime.UtcNow - pending.QueuedUtc > PendingFullscreenRequestExpiry)
            {
                _pendingFullscreenRequests.Remove(rxEndpoint.Key);
                return;
            }

            if (!rxEndpoint.IsMVStateFresh(MultiviewStateFreshness))
                return;

            if (rxEndpoint.ActiveTileCount <= 1)
            {
                _pendingFullscreenRequests.Remove(rxEndpoint.Key);
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Rejected queued fullscreen request for endpoint '{EndpointKey}': activeTiles={ActiveTiles}",
                    _ctl,
                    rxEndpoint.Key,
                    rxEndpoint.ActiveTileCount);
                return;
            }

            if (pending.SourceTileReference > rxEndpoint.ActiveTileCount)
            {
                _pendingFullscreenRequests.Remove(rxEndpoint.Key);
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Rejected queued fullscreen request for endpoint '{EndpointKey}': requested sourceTile={SourceTile}, activeTiles={ActiveTiles}",
                    _ctl,
                    rxEndpoint.Key,
                    pending.SourceTileReference,
                    rxEndpoint.ActiveTileCount);
                return;
            }

            var previousLayout = rxEndpoint.ActivePresetMultiviewLayoutName;
            if (string.IsNullOrWhiteSpace(previousLayout))
            {
                _pendingFullscreenRequests.Remove(rxEndpoint.Key);
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Rejected queued fullscreen request for endpoint '{EndpointKey}': active layout is unknown",
                    _ctl,
                    rxEndpoint.Key);
                return;
            }

            if (!rxEndpoint.TryGetActiveMVTile(pending.SourceTileReference, out var sourceTile) || string.IsNullOrWhiteSpace(sourceTile.SourceReference))
            {
                _pendingFullscreenRequests.Remove(rxEndpoint.Key);
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Rejected queued fullscreen request for endpoint '{EndpointKey}': source for tile {SourceTile} is unknown",
                    _ctl,
                    rxEndpoint.Key,
                    pending.SourceTileReference);
                return;
            }

            const string fullscreenLayout = "1-1";
            if (rxEndpoint.AvailablePresetMultiviewLayouts.Count > 0 && !rxEndpoint.IsKnownPresetMVLayout(fullscreenLayout))
            {
                _pendingFullscreenRequests.Remove(rxEndpoint.Key);
                Debug.LogMessage(
                    Serilog.Events.LogEventLevel.Warning,
                    "Rejected queued fullscreen request for endpoint '{EndpointKey}': fullscreen layout '{Layout}' is unavailable",
                    _ctl,
                    rxEndpoint.Key,
                    fullscreenLayout);
                return;
            }

            ClearFullscreenReturnState(rxEndpoint, "new fullscreen requested");

            _pendingFullscreen[rxEndpoint.Key] = new PendingMultiviewFullscreen
            {
                PreviousLayoutName = previousLayout,
                SourceTileReference = pending.SourceTileReference,
                SourceReference = sourceTile.SourceReference,
                QueuedUtc = DateTime.UtcNow,
            };

            var sent = NhdApiCommandSender.TrySend(_ctl, $"mscene active {rxEndpoint.ApiEndpointReference} {fullscreenLayout}");
            if (sent)
            {
                _pendingFullscreenRequests.Remove(rxEndpoint.Key);
                return;
            }

            _pendingFullscreen.Remove(rxEndpoint.Key);
            Debug.LogMessage(
                Serilog.Events.LogEventLevel.Warning,
                "Failed to dispatch queued fullscreen request for endpoint '{EndpointKey}'",
                _ctl,
                rxEndpoint.Key);
        }

        private bool ShouldBypassFullscreenReturnClearForRoute(NhdBaseDevice rxEndpoint, string txReference, string layoutName, int tileReference)
        {
            if (rxEndpoint == null)
                return false;

            if (!_recentFullscreenRoutes.TryGetValue(rxEndpoint.Key, out var recent))
                return false;

            if (DateTime.UtcNow - recent.RequestedUtc > FullscreenRouteClearBypassWindow)
            {
                _recentFullscreenRoutes.Remove(rxEndpoint.Key);
                return false;
            }

            var isMatch = tileReference == recent.TileReference
                && string.Equals(layoutName?.Trim(), recent.LayoutName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(txReference?.Trim(), recent.TxReference, StringComparison.OrdinalIgnoreCase);

            if (!isMatch)
                return false;

            _recentFullscreenRoutes.Remove(rxEndpoint.Key);

            return true;
        }

        private void ExpirePendingTileRoutes()
        {
            var expiredKeys = _pendingTileRoutes
                .Where(kvp => DateTime.UtcNow - kvp.Value.QueuedUtc > PendingTileRouteExpiry)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _pendingTileRoutes.Remove(key);
            }
        }

        private void ExpirePendingFullscreenRequests()
        {
            var expiredKeys = _pendingFullscreenRequests
                .Where(kvp => DateTime.UtcNow - kvp.Value.QueuedUtc > PendingFullscreenRequestExpiry)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _pendingFullscreenRequests.Remove(key);
            }
        }

        private static IEnumerable<NhdBaseDevice> GetEndpoints()
        {
            return DeviceManager.AllDevices
                .OfType<NhdBaseDevice>()
                .Where(d => d is not NhdCtlPro);
        }

        private void MarkAllEndpointsOffline(string reason)
        {
            var endpoints = GetEndpoints().ToList();
            foreach (var endpoint in endpoints)
            {
                endpoint.SetOnlineState(false);
            }
        }

        private static NhdBaseDevice ResolveEndpoint(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return null;

            var value = reference.Trim();

            return GetEndpoints().FirstOrDefault(d =>
                string.Equals(d.ConfiguredAlias, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Hostname, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.ApiEndpointReference, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Key, value, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNullRouteReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return true;

            var value = reference.Trim();

            return value.Equals("null", StringComparison.OrdinalIgnoreCase)
                || value.Equals(NhdGlobalRouter.RouteOff, StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("none", StringComparison.OrdinalIgnoreCase);
        }
    }
}