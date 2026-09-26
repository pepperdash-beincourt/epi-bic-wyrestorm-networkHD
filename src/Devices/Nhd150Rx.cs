using System.Collections.Generic;
using System.Linq;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Plugin.Routing;

namespace PepperDash.Essentials.Plugin
{
	/// <summary>
	/// NHD-150-RX multiview decoder. Also implements <see cref="IRoutingSource"/> since its
	/// HdmiOutput1 port is effectively the composited output of whatever layout is currently active
	/// - it may be routed on (via tie lines / further midpoints) to its eventual display
	/// destination, rather than assumed to be a fixed direct physical connection. Also implements
	/// <see cref="IHasDynamicMultiviewLayout"/> (defined in Essentials Core) so consumers like a room
	/// plugin can drive dynamic layouts without taking a compile-time dependency on this plugin.
	/// Also implements <see cref="IRoutingSinkWithLayouts"/> so its per-tile sinks can be reached
	/// programmatically via the parent device, in addition to being individually registered with
	/// <see cref="DeviceManager"/> - see <see cref="WindowTileSinks"/> for details. Also implements
	/// <see cref="IRoutingSinkWithLayoutState"/> (<see cref="NhdBaseDevice.CurrentLayout"/> /
	/// <see cref="NhdBaseDevice.LayoutChanged"/>) so a generic, product-agnostic snapshot of the
	/// active canvas/tile geometry and routed sources can be rendered by a React UI or the developer
	/// tools Routing page.
	/// </summary>
	public class Nhd150Rx : NhdBaseDevice, IRoutingSource, IHasDynamicMultiviewLayout, IRoutingSinkWithLayouts, IRoutingSinkWithLayoutState
	{

		/// <summary>
		/// The multiview tile-sink child devices for this decoder, one per potential window slot
		/// (see <see cref="NhdBaseDevice.ConfiguredMaxTileCount"/>), keyed by 1-based tile number.
		/// Each is also registered with <see cref="DeviceManager"/>, so it can be targeted directly by
		/// string-key routing APIs (tie lines / <c>IRunDirectRouteAction.RunDirectRoute</c>) as well as
		/// programmatically via this dictionary (per <see cref="IRoutingSinkWithLayouts"/>).
		/// </summary>
		public Dictionary<int, IRoutingSinkWithFeedback> WindowTileSinks { get; } = new Dictionary<int, IRoutingSinkWithFeedback>();

		public override bool IsTransmitter => false;
		public override bool SupportsCec => true;
		public override bool SupportsIr => false;
		public override bool Supports232 => true;
		public override int MaxStreamCount => 9;

		/// <summary>
		/// The multiview tile-sink child devices for this decoder, one per potential window slot.
		/// Thin, concretely-typed view over <see cref="WindowTileSinks"/> for callers within this
		/// plugin that need <see cref="NhdMultiviewTileSink"/>-specific members.
		/// </summary>
		public IReadOnlyList<NhdMultiviewTileSink> TileSinks
			=> WindowTileSinks.Values.OfType<NhdMultiviewTileSink>().ToList();

		public Nhd150Rx(string key, string name, NhdDeviceProperties config)
			: base(key, name, config, "NHD-150-RX")
		{
			AddStreamInputPort();
			AddHdmiOutputPort(NhdPortKeys.HdmiOutput1);
			AddAnalogAudioOutputPort();

			for (var tileNumber = 1; tileNumber <= ConfiguredMaxTileCount; tileNumber++)
			{
				var tileSink = new NhdMultiviewTileSink(this, tileNumber);
				WindowTileSinks.Add(tileNumber, tileSink);
				DeviceManager.AddDevice(tileSink);
			}
		}

		/// <summary>
		/// Gets the tile-sink for the given 1-based tile number, or null if out of range.
		/// </summary>
		public NhdMultiviewTileSink GetTileSink(int tileNumber)
			=> WindowTileSinks.TryGetValue(tileNumber, out var tileSink) ? tileSink as NhdMultiviewTileSink : null;

		/// <summary>
		/// Computes a multiview layout at runtime from a set of participant sources with priority
		/// values (see <see cref="NhdDynamicMultiviewLayoutCalculator"/>), sends it to the decoder,
		/// and syncs each tile-sink's feedback (<see cref="NhdMultiviewTileSink.UpdateCurrentSourceState"/>)
		/// to match. Additive to the existing named preset/custom layout APIs - does not affect them.
		/// </summary>
		/// <param name="participantSources">Sources to place in participant tiles, each with a priority (lower = higher priority).</param>
		/// <param name="presentationSourceKey">Essentials device key for the active presentation source, or null/empty if no presentation is active.</param>
		public bool ApplyDynamicLayout(
			IReadOnlyList<MultiviewParticipantSource> participantSources,
			string presentationSourceKey)
		{
			if (!SupportsMultiview)
			{
				Debug.LogError("[{0}] Endpoint does not support dynamic multiview layout", Key);
				return false;
			}

			var ctl = DeviceManager.AllDevices.OfType<NhdCtlPro>().FirstOrDefault();
			if (ctl?.SessionManager == null)
			{
				Debug.LogError("[{0}] NHD-CTL session manager is not available for dynamic multiview layout", Key);
				return false;
			}

			TryGetHdmiOutResolutionDimensions(out _, out _);

			// A presentation source behind a switching device (for example a document camera's HDMI
			// input) needs that device switched to it; the layout below only names the transmitter.
			// Only the presentation source is selected: participant tiles never switch shared devices.
			if (!string.IsNullOrWhiteSpace(presentationSourceKey))
				NhdSourcePath.SelectPath(presentationSourceKey, this);

			// The NHD multiview coordinate space is the fixed 1920x1080 reference canvas the layouts are
			// authored against (see NhdDynamicMultiviewLayoutCalculator) - NOT the decoder's HDMI-out
			// resolution. Scaling tiles up to a 4K output pushes them outside the multiview canvas and the
			// decoder shows nothing, so always lay out against the reference canvas.
			var tiles = NhdDynamicMultiviewLayoutCalculator.CalculateLayout(
				participantSources,
				presentationSourceKey,
				NhdDynamicMultiviewLayoutCalculator.DefaultCanvasWidth,
				NhdDynamicMultiviewLayoutCalculator.DefaultCanvasHeight,
				ConfiguredMaxTileCount);

			if (!ctl.SessionManager.TryApplyDynamicLayout(this, this, tiles))
				return false;

			SyncTileSinksToLayout(tiles);
			return true;
		}

		/// <summary>
		/// Devjson/console-friendly overload of
		/// <see cref="ApplyDynamicLayout(IReadOnlyList{MultiviewParticipantSource}, string)"/>.
		/// The reflection-based devjson dispatcher (<see cref="DeviceJsonApi"/>) can only construct
		/// true C# arrays of primitives/enums from JSON - it can't build an
		/// <see cref="IReadOnlyList{T}"/> of the <c>MultiviewParticipantSource</c> POCO. This takes
		/// parallel primitive arrays instead (<paramref name="sourceKeys"/>[i] paired with
		/// <paramref name="priorities"/>[i]) so the batch API is directly testable via devjson.
		/// </summary>
		public bool ApplyDynamicLayout(string[] sourceKeys, int[] priorities, string presentationSourceKey)
		{
			if (sourceKeys == null || priorities == null || sourceKeys.Length != priorities.Length)
			{
				Debug.LogError("[{0}] sourceKeys and priorities must both be provided and the same length", Key);
				return false;
			}

			var participantSources = sourceKeys
				.Select((key, i) => new MultiviewParticipantSource(key, priorities[i]))
				.ToList();

			return ApplyDynamicLayout(participantSources, presentationSourceKey);
		}

		/// <summary>
		/// Eagerly syncs each tile-sink's current-source feedback to the given layout, and updates
		/// this device's <see cref="NhdBaseDevice.CurrentLayout"/>/<see cref="NhdBaseDevice.LayoutChanged"/>
		/// state to match (via <see cref="NhdBaseDevice.SetMVRuntimeState(NhdMultiStreamMode, System.Collections.Generic.IReadOnlyList{NhdMultiviewTileState})"/>),
		/// ahead of hardware feedback confirming the same state (see
		/// <c>NhdCtlSessionManager.FinalizePendingMviewInformationEntry</c>), so consumers (tile-sink
		/// routing feedback and the generic multiview layout state) reflect a just-applied dynamic
		/// layout immediately rather than waiting on a round trip to the decoder.
		/// </summary>
		private void SyncTileSinksToLayout(IReadOnlyList<NhdMultiviewTileState> tiles)
		{
			var tilesByNumber = (tiles ?? new List<NhdMultiviewTileState>())
				.Where(t => t != null)
				.ToDictionary(t => t.TileNumber);

			foreach (var tileSink in WindowTileSinks.Values.OfType<NhdMultiviewTileSink>())
			{
				tilesByNumber.TryGetValue(tileSink.TileNumber, out var tile);

				var sourceKey = tile?.SourceReference;
				var sourceDevice = string.IsNullOrWhiteSpace(sourceKey)
					? null
					: DeviceManager.GetDeviceForKey(sourceKey) as IRoutingSource;

				tileSink.UpdateCurrentSourceState(eRoutingSignalType.Video, sourceDevice);
			}

			SetMVRuntimeState(MultiStreamMode, tiles);
		}
	}
}
