![PepperDash Essentials Plugin Logo](/images/essentials-plugin-blue.png)

# WyreStorm NetworkHD Essentials Plugin

Essentials plugin for WyreStorm NetworkHD endpoints and controller-driven matrix routing.

## License

Provided under MIT license. See [LICENSE.md](LICENSE.md).

## Overview

This plugin provides:

- Device support for WyreStorm NetworkHD controller, transmitter, and receiver models in this repository.
- Matrix routing integration through a global router instance.
- Route and device feedback tracking across video, audio, and USB domains.
- Multiview layout control and tracking for supported decoders.
- Session-aware CTL command handling (login/readiness, queueing, throttled refresh, and feedback parsing).

## Supported Device Types

The following Essentials type names are currently registered:

| Device | Type Names | Notes |
| --- | --- | --- |
| NHD-CTL-PRO | nhd-ctl-pro, nhdctlpro | Controller transport, session lifecycle, matrix and notification parsing |
| NHD-120-TX | nhd-120-tx, nhd120tx | Transmitter with HDMI input, analog audio input, stream output |
| NHD-150-RX | nhd-150-rx, nhd150rx | Receiver with stream input, HDMI output, analog audio output, multiview support |



## Requirements

- .NET SDK that can build net8 projects.
- PepperDash Essentials environment for runtime deployment.
- Declared minimum Essentials framework version in factories: 3.0.0.
- Current package reference in this repository: PepperDashEssentials 3.0.0-dev-v3-routing.50.

## Build and Package

From repository root:

1. Restore packages:
	 dotnet restore .\epi-wyrestorm-networkHD.4Series.sln
2. Build:
	 dotnet build .\epi-wyrestorm-networkHD.4Series.sln -c Debug

On build, the project generates a CPLZ package in the output folder:

- output/epi-wyrestorm-networkHD.4Series.<Version>.cplz

Version and package metadata are defined in:

- src/Directory.Build.props
- src/epi-wyrestorm-networkHD.4Series.csproj
<br>
## Configuration

### Controller (NHD-CTL-PRO)

The CTL device is required for command transport and feedback parsing.

Example skeleton:

```json
{
	"key": "nhdCtl",
	"name": "NHD Controller",
	"type": "nhd-ctl-pro",
	"group": "nhd",
	"properties": {
        "control": {
            "method": "tcpIp",
            "tcpSshProperties": {
                "address": "192.168.1.50",
                "port": 23,
                "autoReconnect": true,
                "autoReconnectIntervalMs": 10000
            }
        },
        "customMultiviewLayouts": [],
        "multiviewPresets": []
    }
}
```

Notes:

- The CTL can be controlled over RS-232, Telnet over TCP/IP (port 23) or SSH (port 10022).
- Username/password credentials are only required for SSH.
- For SSH, API credentials can be set directly on properties.apiUsername / properties.apiPassword. If those values are omitted, this plugin also hydrates credentials from control.tcpSshProperties username/password.
<br>

### Transmitter (NHD-120-TX)

```json
{
	"key": "nhdTx1",
	"name": "TX 01",
	"type": "nhd-120-tx",
	"group": "nhd",
	"properties": {
		"matrixInputSlot": 1,
		"alias": "Tx1"
	}
}
```
<br>

### Receiver (NHD-150-RX)

```json
{
	"key": "nhdRx1",
	"name": "RX 01",
	"type": "nhd-150-rx",
	"group": "nhd",
	"properties": {
		"matrixOutputSlot": 1,
		"alias": "nhdRx1",
        "customMultiviewLayouts": [],
        "multiviewPresets": []
	}
}
```
<br>

### Core Properties Reference

| Property | Type | Applies To | Description |
| --- | --- | --- | --- |
| matrixInputSlot | number | TX | Matrix input slot number used for transmitter routing |
| matrixOutputSlot | number | RX | Matrix output slot number used for receiver routing |
| alias | string | TX, RX | Must match the endpoint's defined alias for CTL/API commands |
| apiUsername | string | CTL | API login username |
| apiPassword | string | CTL | API login password |
| customMultiviewLayouts | array | RX, CTL | Named custom multiview geometry profiles |
| multiviewPresets | array | RX, CTL | Named preset workflows (layout + optional window routing/audio policy) |


<br>

## Multiview

NHD-150-RX supports multiview (max stream count 9 in this plugin).

### Custom Layout Example

```json
"customMultiviewLayouts": [
	{
		"key": "quad",
		"displayName": "Quad 2x2",
		"mode": "Tile",
		"canvasWidth": 1920,
		"canvasHeight": 1080,
		"audioMode": "Window",
		"audioWindowReference": 1,
		"windows": [
			{ "windowReference": 1, "x": 0,   "y": 0,   "width": 960, "height": 540, "scale": "Fit" },
			{ "windowReference": 2, "x": 960, "y": 0,   "width": 960, "height": 540, "scale": "Fit" },
			{ "windowReference": 3, "x": 0,   "y": 540, "width": 960, "height": 540, "scale": "Fit" },
			{ "windowReference": 4, "x": 960, "y": 540, "width": 960, "height": 540, "scale": "Fit" }
		]
	}
]
```
### Preset Example

```json
"multiviewPresets": [
	{
		"key": "quad-default",
		"displayName": "Quad Default",
		"layoutSource": "Config",
		"layout": "quad",
		"windowRoutes": [
			{ "windowReference": 1, "txKey": "nhdTx1" },
			{ "windowReference": 2, "txKey": "nhdTx2" },
			{ "windowReference": 3, "txKey": "nhdTx3" },
			{ "windowReference": 4, "txKey": "nhdTx4" }
		],
		"audioMode": "Window",
		"audioWindowReference": 1
	}
]
```

### Preset Notes

| Item | Values | Notes |
| --- | --- | --- |
| layoutSource | Controller, Config | Controller - built in or custom layout stored on CTL<br> Config - from customMultiviewLayouts |
| mode | Tile, Overlay | Tile - Windows defined in config will not overlap each other <br> Overlay - Windows defined in config will overlap each other <br><br> Note: setting this incorrectly may have adverse effects on rendering content |
| scale | Fit, Stretch | Window scaling options |
| audioMode | Window, Separate, NoChange | Window - select a window index to be the audio source <br> Separate - select a transmitter endpoint to be the audio source <br> NoChange - do not make an audio selection when recalling this Preset |
| audioWindowReference | Integer index of selected tile in Preset | Must be defined when audioMode is set to Window |
| audioTxKey | Transmitter device key  | must be defined when audioMode is set to Separate |
| preset resolution order | RX-local first, then controller-level fallback | Local receiver definitions override controller definitions |

Note: When multiple multiview receivers in a system may use the same layouts/presets, it may be easier to define them in once the CTL device so they can be shared without data replication in config. When presets or layouts with the same key are defined in both the CTL device and the Rx device, the Rx device takes precedence. 

<br>

## Routing and Feedback Behavior

- A global router singleton is auto-registered under key NhdRouter.
- Matrix clear route aliases accepted by router input resolution: none, null, off, $off.
- Single-stream matrix switching is enforced only for outputs that support it.
	- Multiview outputs are excluded from single-stream matrix switching.
- CTL session bootstrap requests matrix state, device list, and device status. Endpoint notifications (online/offline, video found/lost, etc.) are unsolicited and pushed automatically by the controller over the open session; no subscribe command is sent.
- Matrix state refresh is request-driven and includes a periodic refresh every 30 seconds when session-ready and connected.
- Matrix refresh requests are throttled (2 seconds).
- Video lost notifications are debounced (10 seconds) to avoid sync flap churn.

<br>

## NhdRouter Basic Route Commands

| Function | Signature | Notes |
| --- | --- | --- |
| Route | `void Route(string inputSlotKey, string outputSlotKey, eRoutingSignalType type)` | Primary matrix routing entry point on NhdRouter |
| Route by slot | `void RouteBySlot(int inputSlot, int outputSlot, eRoutingSignalType type)` | Routes using configured `matrixInputSlot` and `matrixOutputSlot` values |
| Clear route | `Route(clearAlias, outputSlotKey, type)` | clearAlias values: none, null, off, $off |

Note: These methods are invoked on the NdhRouter device <br> `type` allowed values are `eRoutingSignalType.AudioVideo`, `eRoutingSignalType.Video`, `eRoutingSignalType.Audio`


<br>

## Multiviewer Specific Commands

Common endpoint methods available through device classes include:

| Function | MV Rx Endpoint Method | Notes |
| --- | --- | --- |
| Route MV tile with layout | `bool RouteMVTile(string inputSlotKey, string outputSlotKey, string layoutName, int tileReference)` | Routes a source slot to a specific MV tile within the named layout |
| Reprobe layouts | `bool ReprobeMVLayouts()` | queries an endpoint for its preconfigured and custom layouts defined in the CTL |
| Refresh layout list | `bool RefreshMVLayouts()` | Forces a controller `mscene get` for the endpoint and learns its available preset layouts |
| Query layouts with ids | `Dictionary<string,object> GetMVLayoutsWithIds()` / `string GetMVLayoutsWithIdsJson()` | Returns available preset/custom layouts with stable ids (`preset:<id>`, `custom:<key>`) and the active layout |
| Recall layout by id | `bool RecallMVLayout(string id)` | Recalls a layout by the id from the query. Accepts the raw layoutId (`2-1`, `test1`), the prefixed id (`preset:2-1`), or `custom:<key>` |
| Apply custom layout | `bool ApplyCustomMVLayout(string layoutKey)` |  |
| Apply custom layout with sources | `bool ApplyCustomMVLayoutWithSources(string layoutKey, IDictionary<int, string> sourceReferencesByWindow)` | used by the apply preset function |
| Apply preset | `bool ApplyMVPreset(string presetKey)` |  |
| Fullscreen tile | `bool FullscreenMVTile(int sourceTileReference)` |  |
| Return from fullscreen | `bool ReturnFromMVFullscreen()` |  |

These ultimately execute through the CTL session manager command pipeline.

<br>

## Troubleshooting

- No routing/control response:
	- Verify an NHD-CTL-PRO device is present and connected.
	- Verify control transport settings and credentials.
- Session never reaches ready:
	- Check for User:/Password: prompts and configure apiUsername/apiPassword.
	- Telnet credential prompts are handled automatically by the plugin.
- Feedback appears delayed after external matrix changes:
	- This plugin applies a periodic matrix state check (30 seconds) plus event-driven refreshes.
- Multiview preset/layout rejects:
	- Validate layout keys, window references (1-based), and transmitter device keys.

<br>

## Repository Layout

- src/: plugin source
- _docs/: vendor API and technical reference notes
- images/: repository images
- output/: generated local build/package artifacts

<br>

## Contributing

Issues and pull requests are welcome. Please include:

- Firmware/build context
- Device model(s)
- Relevant config snippets
- Log excerpts showing the command/response sequence

<br>

## Development Note

IR/232 functionality is currently under development and intentionally omitted from this README for now.
<!-- START Minimum Essentials Framework Versions -->

<!-- END Minimum Essentials Framework Versions -->
<!-- START Config Example -->
### Config Example

```json
{
    "key": "GeneratedKey",
    "uid": 1,
    "name": "GeneratedName",
    "type": "MockNhdRxProperties",
    "group": "Group",
    "properties": {
        "tileCount": 0
    }
}
```
<!-- END Config Example -->
<!-- START Supported Types -->

<!-- END Supported Types -->
<!-- START Join Maps -->

<!-- END Join Maps -->
<!-- START Interfaces Implemented -->
### Interfaces Implemented

- IRoutingSource
- IHasDynamicMultiviewLayout
- IRoutingSinkWithLayouts
- IRoutingSinkWithLayoutState
- IRoutingSinkWithFeedback
- IRoutingMidpoint
- INhdInputSlot
- IKeyName
- IRoutingMidpointWithFeedback
<!-- END Interfaces Implemented -->
<!-- START Base Classes -->
### Base Classes

- NhdBaseDeviceFactory<Nhd120Tx>
- NhdBaseDeviceFactory<Nhd150Rx>
- NhdBaseDeviceFactory<NhdCtlPro>
- Enumeration<AudioOutputEnum>
- Enumeration<VideoOutputEnum>
- Enumeration<AudioInputEnum>
- Enumeration<DeviceInputEnum>
- Enumeration<VideoInputEnum>
- EventArgs
- StatusMonitorBase
- NhdBaseDevice
- EssentialsDevice
- JsonConverter
<!-- END Base Classes -->
<!-- START Public Methods -->
### Public Methods

- public int CompareTo(Enumeration<TEnum> other)
- public int CompareTo(object other)
- public void ProbeSessionHealth(string reason = null)
- public void HandleCtlTransportConnectionChanged(bool isConnected)
- public void StartSessionLifecycle()
- public void StopSessionLifecycle()
- public bool TryRouteMVTile(IKeyed requestedBy, NhdBaseDevice txEndpoint, NhdBaseDevice rxEndpoint, string layoutName, int tileReference)
- public MultiviewTileRouteResult RouteMVTileGuarded(IKeyed requestedBy, NhdBaseDevice txEndpoint, NhdBaseDevice rxEndpoint, string layoutName, int tileReference)
- public bool TryActivateMVLayout(IKeyed requestedBy, NhdBaseDevice rxEndpoint, string layoutName)
- public bool TryApplyCustomMVLayout(IKeyed requestedBy, NhdBaseDevice rxEndpoint, string layoutKey)
- public bool TryApplyCustomMVLayoutWithSources(
            IKeyed requestedBy,
            NhdBaseDevice rxEndpoint,
            string layoutKey,
            IDictionary<int, string> sourceReferencesByWindow)
- public bool TryApplyDynamicLayout(IKeyed requestedBy, NhdBaseDevice rxEndpoint, IReadOnlyList<NhdMultiviewTileState> tiles)
- public bool TryApplyMVPreset(IKeyed requestedBy, NhdBaseDevice rxEndpoint, string presetKey)
- public bool TryFullscreenMVTile(IKeyed requestedBy, NhdBaseDevice rxEndpoint, int sourceTileReference)
- public bool TryReturnFromMVFullscreen(IKeyed requestedBy, NhdBaseDevice rxEndpoint)
- public bool TryGetMVFullscreenReturnLayout(NhdBaseDevice rxEndpoint, out string layoutName)
- public bool TryProbeAndLearnMVLayouts(IKeyed requestedBy, NhdBaseDevice rxEndpoint)
- public bool TryReprobeAndLearnMVLayouts(IKeyed requestedBy, NhdBaseDevice rxEndpoint)
- public void RequestMVLayoutList(NhdBaseDevice endpoint, IKeyed source = null)
- public NhdMultiviewTileState WithSourceReference(string resolvedSourceReference)
- public void SetResolvedHostname(string hostname)
- public void SetOnlineState(bool isOnline)
- public void SetInputSyncState(bool hasSync)
- public bool TryGetHdmiOutResolutionDimensions(out int width, out int height)
- public void SetHdmiOutResolution(string resolution)
- public bool TryGetCustomMVLayout(string layoutKey, out NhdCustomMultiviewLayoutProperties layout)
- public bool TryGetMVPreset(string presetKey, out NhdMultiviewPresetProperties preset)
- public bool IsMVStateFresh(TimeSpan maxAge)
- public void SetMVRuntimeState(NhdMultiStreamMode mode, int activeTileCount)
- public void SetMVRuntimeState(NhdMultiStreamMode mode, IReadOnlyList<NhdMultiviewTileState> tiles)
- public bool TryGetActiveMVTile(int tileReference, out NhdMultiviewTileState tile)
- public void SetActiveMVAudioWindow(int? windowReference)
- public void SetActiveMVAudioSeparateSource(string sourceReference)
- public void SetPresetLayoutAudioWindow(string layoutName, int? windowReference)
- public void SetPresetLayoutAudioSeparateSource(string layoutName, string sourceReference)
- public void ApplyPresetLayoutAudioSetting(string layoutName)
- public void SetAvailablePresetMVLayouts(IEnumerable<string> layoutNames)
- public bool IsKnownPresetMVLayout(string layoutName)
- public bool TryCaptureActiveLayoutGeometry(string layoutName)
- public void ClearLearnedPresetLayoutGeometrySignatures()
- public bool TryIdentifyPresetLayoutByActiveGeometry(out string layoutName)
- public bool TryIdentifyCustomLayoutByActiveGeometry(out string layoutKey)
- public void SetActivePresetMVLayout(string layoutName, bool inferred = false)
- public void SetActiveCustomMVLayout(string layoutKey, bool inferred = false)
- public bool ReprobeMVLayouts()
- public bool RefreshMVLayouts()
- public bool ApplyCustomMVLayout(string layoutKey)
- public bool ApplyCustomMVLayoutWithSources(string layoutKey, IDictionary<int, string> sourceReferencesByWindow)
- public bool ApplyMVPreset(string presetKey)
- public bool RecallMVLayout(string id)
- public string GetMVLayoutsWithIdsJson()
- public string GetEndpointConfigJson()
- public bool FullscreenMVTile(int sourceTileReference)
- public bool ReturnFromMVFullscreen()
- public bool TryGetMVFullscreenReturnLayout(out string layoutName)
- public void ExecuteSwitch(object inputSelector, object outputSelector, eRoutingSignalType signalType)
- public void ClearRoute(object outputSelector, eRoutingSignalType signalType)
- public NhdMultiviewTileSink GetTileSink(int tileNumber)
- public bool ApplyDynamicLayout(
			IReadOnlyList<MultiviewParticipantSource> participantSources,
			string presentationSourceKey)
- public bool ApplyDynamicLayout(string[] sourceKeys, int[] priorities, string presentationSourceKey)
- public MockNhdMultiviewTileSink GetTileSink(int tileNumber)
- public bool ApplyDynamicLayout(
			IReadOnlyList<MultiviewParticipantSource> participantSources,
			string presentationSourceKey)
- public bool ApplyDynamicLayout(string[] sourceKeys, int[] priorities, string presentationSourceKey)
- public void ExecuteSwitch(object inputSelector)
- public void SetCurrentSource(eRoutingSignalType signalType, IRoutingSource sourceDevice)
- public void UpdateCurrentSourceState(eRoutingSignalType signalType, IRoutingSource sourceDevice)
- public void SetInputRoute(eRoutingSignalType type, INhdInputSlot input)
- public void ExecuteSwitch(object inputSelector)
- public void SetCurrentSource(eRoutingSignalType signalType, IRoutingSource sourceDevice)
- public void UpdateCurrentSourceState(eRoutingSignalType signalType, IRoutingSource sourceDevice)
- public void ExecuteSwitch(object inputSelector, object outputSelector, eRoutingSignalType signalType)
- public void ClearRoute(object outputSelector, eRoutingSignalType signalType)
- public void ExecuteNumericSwitch(ushort input, ushort output, eRoutingSignalType type)
- public void RouteBySlot(int inputSlot, int outputSlot, eRoutingSignalType type)
- public bool TrySetTrackedMatrixRoute(string txEndpointKey, string rxEndpointKey, eRoutingSignalType signalType)
- public void Route(string inputSlotKey, string outputSlotKey, eRoutingSignalType type)
- public bool ApplyControllerMVLayout(string outputSlotKey, string layoutName)
- public bool ActivateMVLayout(string outputSlotKey, string layoutName)
- public bool ApplyCustomMVLayout(string outputSlotKey, string layoutKey)
- public bool ApplyCustomMVLayoutWithContent(
        string outputSlotKey,
        string layoutKey,
        IDictionary<int, string> inputSlotKeysByWindow)
- public bool ApplyMVPreset(string outputSlotKey, string presetKey)
- public bool TryGetTrackedMVLayout(string outputSlotKey, out string layoutName, out bool inferred)
- public bool TryGetTrackedCustomMVLayout(string outputSlotKey, out string layoutKey, out bool inferred)
- public bool ProbeAndLearnMVLayouts(string outputSlotKey)
- public bool RouteMVTile(string inputSlotKey, string outputSlotKey, int tileReference)
- public bool FullscreenMVTile(string outputSlotKey, int sourceTileReference)
- public bool ReturnFromMVFullscreen(string outputSlotKey)
- public bool TryGetMVFullscreenReturnLayout(string outputSlotKey, out string layoutName)
- public bool RouteMVTile(string inputSlotKey, string outputSlotKey, string layoutName, int tileReference)
- public bool TryExecute(IKeyed source, INhdInputSlot inputSlot, NhdMatrixOutput output, eRoutingSignalType signalType, out eRoutingSignalType routedSignalType)
- public bool TryExecute(IKeyed source, INhdInputSlot inputSlot, NhdMatrixOutput output, eRoutingSignalType signalType)
- public bool TryExecute(IKeyed source, INhdInputSlot inputSlot, NhdMatrixOutput output, eRoutingSignalType signalType)
- public void Base_Factory_Defines_MinimumEssentialsVersion_As_3_0_0()
- public void Factory_Assigns_MinimumEssentialsFrameworkVersion(string factoryClassName)
- public void Factory_Sets_TypeNames(string factoryClassName)
- public void Factory_Source_Contains_TypeName(string factoryClassName, string typeName)
- public void No_Duplicate_TypeNames_Across_Factories()
- public void Assembly_Loads_Successfully()
- public void Assembly_Name_Is_Expected()
- public void Factory_Count_Is_Six()
- public void Factory_Exists_ByName(string factoryClassName)
- public void All_Factories_Have_Parameterless_Constructor()
- public void Config_Class_Exists(string fullName)
- public void Config_Has_Parameterless_Constructor(string fullName)
- public void NhdDeviceProperties_Property_Type_Matches(string propertyName, string expectedTypeName)
- public void NhdDeviceProperties_List_Property_Element_Type(string propertyName, string expectedElementType)
- public void NhdDeviceProperties_Deserializes_Sample_Json()
- public void Calculator_Type_Exists()
- public void Calculator_Is_Static()
- public void CalculateLayout_Method_Exists_With_Expected_Signature()
- public void Grid_Layout_Uses_Ceiling_Sqrt_For_Column_Count()
- public void Presentation_Tile_Is_Always_Tile_Number_One()
- public void Participants_Are_Ordered_By_Priority_Ascending()
- public void Overflow_Participants_Are_Clamped_To_Available_Tile_Capacity()
- public void Nhd150Rx_Type_Exists()
- public void ApplyDynamicLayout_ParticipantSource_Overload_Exists()
- public void ApplyDynamicLayout_Devjson_Friendly_Overload_Exists()
<!-- END Public Methods -->
<!-- START Bool Feedbacks -->
### Bool Feedbacks

- IsOnline
- InputSyncDetected
- IsOnline
- IsOnline
- IsOnline
- IsOnline
<!-- END Bool Feedbacks -->
<!-- START Int Feedbacks -->

<!-- END Int Feedbacks -->
<!-- START String Feedbacks -->

<!-- END String Feedbacks -->
