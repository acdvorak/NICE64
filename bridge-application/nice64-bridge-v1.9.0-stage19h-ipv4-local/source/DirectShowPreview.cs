namespace PicoSwitchController;

using DirectShowLib;
using System.Runtime.InteropServices;

internal sealed class DirectShowPreview : IDisposable
{
    private const int HundredNanosecondsPerSecond = 10_000_000;

    private readonly IFilterGraph2 _graph;
    private readonly ICaptureGraphBuilder2 _builder;
    private readonly IBaseFilter _source;
    private readonly IMediaControl _mediaControl;
    private readonly IVideoWindow _videoWindow;

    public DirectShowPreview(int deviceIndex, int width, int height, int fps, IntPtr owner)
    {
        DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        if (deviceIndex < 0 || deviceIndex >= devices.Length)
        {
            throw new InvalidOperationException($"Video device {deviceIndex} was not found.");
        }

        _graph = (IFilterGraph2)new FilterGraph();
        _builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        DsError.ThrowExceptionForHR(_builder.SetFiltergraph((IGraphBuilder)_graph));

        DsError.ThrowExceptionForHR(_graph.AddSourceFilterForMoniker(
            devices[deviceIndex].Mon,
            null,
            devices[deviceIndex].Name,
            out _source));

        TrySetFormat(width, height, fps);

        int hr = _builder.RenderStream(PinCategory.Preview, MediaType.Video, _source, null, null);
        if (hr < 0)
        {
            hr = _builder.RenderStream(PinCategory.Capture, MediaType.Video, _source, null, null);
        }

        DsError.ThrowExceptionForHR(hr);

        _mediaControl = (IMediaControl)_graph;
        _videoWindow = (IVideoWindow)_graph;
        SetOwner(owner);
    }

    public void Start()
    {
        DsError.ThrowExceptionForHR(_mediaControl.Run());
    }

    public void SetOwner(IntPtr owner)
    {
        DsError.ThrowExceptionForHR(_videoWindow.put_Visible(OABool.False));
        DsError.ThrowExceptionForHR(_videoWindow.put_Owner(owner));
        DsError.ThrowExceptionForHR(_videoWindow.put_WindowStyle(WindowStyle.Child | WindowStyle.ClipChildren | WindowStyle.ClipSiblings));
        DsError.ThrowExceptionForHR(_videoWindow.put_MessageDrain(owner));
        DsError.ThrowExceptionForHR(_videoWindow.put_Visible(OABool.True));
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _videoWindow.SetWindowPosition(0, 0, width, height);
    }

    public void Dispose()
    {
        try
        {
            _mediaControl.Stop();
            _videoWindow.put_Visible(OABool.False);
            _videoWindow.put_Owner(IntPtr.Zero);
        }
        catch
        {
            // Best-effort shutdown for COM graph teardown.
        }

        Marshal.ReleaseComObject(_source);
        Marshal.ReleaseComObject(_builder);
        Marshal.ReleaseComObject(_graph);
    }

    private void TrySetFormat(int width, int height, int fps)
    {
        object? configObject = null;
        try
        {
            int hr = _builder.FindInterface(
                PinCategory.Capture,
                MediaType.Video,
                _source,
                typeof(IAMStreamConfig).GUID,
                out configObject);

            if (hr < 0 || configObject is not IAMStreamConfig streamConfig)
            {
                return;
            }

            DsError.ThrowExceptionForHR(streamConfig.GetNumberOfCapabilities(out int count, out int size));
            IntPtr taskMemPointer = Marshal.AllocCoTaskMem(size);

            try
            {
                for (int i = 0; i < count; i++)
                {
                    AMMediaType? mediaType = null;
                    try
                    {
                        hr = streamConfig.GetStreamCaps(i, out mediaType, taskMemPointer);
                        if (hr < 0 || mediaType is null || mediaType.formatPtr == IntPtr.Zero)
                        {
                            continue;
                        }

                        if (mediaType.formatType != FormatType.VideoInfo)
                        {
                            continue;
                        }

                        VideoInfoHeader? parsedVideoInfo = Marshal.PtrToStructure<VideoInfoHeader>(mediaType.formatPtr);
                        if (parsedVideoInfo is null)
                        {
                            continue;
                        }

                        VideoInfoHeader videoInfo = parsedVideoInfo;
                        if (videoInfo.BmiHeader.Width != width || Math.Abs(videoInfo.BmiHeader.Height) != height)
                        {
                            continue;
                        }

                        videoInfo.AvgTimePerFrame = HundredNanosecondsPerSecond / Math.Max(1, fps);
                        Marshal.StructureToPtr(videoInfo, mediaType.formatPtr, false);
                        streamConfig.SetFormat(mediaType);
                        return;
                    }
                    finally
                    {
                        if (mediaType is not null)
                        {
                            DsUtils.FreeAMMediaType(mediaType);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(taskMemPointer);
            }
        }
        catch
        {
            // Some devices reject format selection; preview can still run at its default.
        }
        finally
        {
            if (configObject is not null)
            {
                Marshal.ReleaseComObject(configObject);
            }
        }
    }
}
