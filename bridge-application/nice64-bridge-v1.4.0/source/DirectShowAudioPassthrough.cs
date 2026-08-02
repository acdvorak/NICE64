namespace PicoSwitchController;

using DirectShowLib;
using System.Runtime.InteropServices;

internal sealed class DirectShowAudioPassthrough : IDisposable
{
    private readonly IFilterGraph2 _graph;
    private readonly ICaptureGraphBuilder2 _builder;
    private readonly IBaseFilter _source;
    private readonly IMediaControl _mediaControl;
    private readonly IBasicAudio? _basicAudio;

    public DirectShowAudioPassthrough(int deviceIndex)
    {
        DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.AudioInputDevice);
        if (deviceIndex < 0 || deviceIndex >= devices.Length)
        {
            throw new InvalidOperationException($"Audio device {deviceIndex} was not found.");
        }

        _graph = (IFilterGraph2)new FilterGraph();
        _builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        DsError.ThrowExceptionForHR(_builder.SetFiltergraph((IGraphBuilder)_graph));

        DsError.ThrowExceptionForHR(_graph.AddSourceFilterForMoniker(
            devices[deviceIndex].Mon,
            null,
            devices[deviceIndex].Name,
            out _source));

        int hr = _builder.RenderStream(PinCategory.Preview, MediaType.Audio, _source, null, null);
        if (hr < 0)
        {
            hr = _builder.RenderStream(PinCategory.Capture, MediaType.Audio, _source, null, null);
        }

        DsError.ThrowExceptionForHR(hr);
        _mediaControl = (IMediaControl)_graph;
        _basicAudio = _graph as IBasicAudio;
    }

    public void Start()
    {
        DsError.ThrowExceptionForHR(_mediaControl.Run());
    }

    public void SetVolumePercent(int percent)
    {
        if (_basicAudio is null)
        {
            return;
        }

        int clamped = Math.Clamp(percent, 0, 100);
        int directShowVolume = clamped == 0
            ? -10_000
            : (int)Math.Round(2_000.0 * Math.Log10(clamped / 100.0));

        directShowVolume = Math.Clamp(directShowVolume, -10_000, 0);

        try
        {
            _basicAudio.put_Volume(directShowVolume);
        }
        catch
        {
            // Some renderers do not expose per-graph volume control.
        }
    }

    public void Dispose()
    {
        try
        {
            _mediaControl.Stop();
        }
        catch
        {
            // Best-effort shutdown for COM graph teardown.
        }

        Marshal.ReleaseComObject(_source);
        Marshal.ReleaseComObject(_builder);
        Marshal.ReleaseComObject(_graph);
    }
}
