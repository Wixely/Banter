using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.Util;
using Android.OS;
using Android.Views;
using Banter.App;
using Java.Lang;

namespace Banter.App.Android;

/// <summary>
/// Points the camera at a QR code and answers with the first one that could be a server.
///
/// <para><b>Its own activity, not a panel in the app.</b> CupriFace owns a GL surface and draws
/// every pixel of it; a camera preview is a second surface the system compositor puts behind or in
/// front of that, and reconciling the two is a fight with no prize. An activity started for a
/// result is also the pattern the file picker already uses here, which works.</para>
/// </summary>
[Activity(Label = "Scan a code", Exported = false)]
public sealed class ScannerActivity : Activity
{
    /// <summary>Where the decoded text is returned, when there is any.</summary>
    public const string ServerExtra = "banter.scanned.server";

    private const string LogTag = "Banter";

    /// <summary>
    /// Big enough to resolve a link's modules, small enough to decode inside a frame interval. A
    /// link fills a version-15 symbol — 77 modules — so 1280 across leaves about 16 pixels per
    /// module at arm's length, which is plenty, while 1920 mostly buys decode time.
    /// </summary>
    private static readonly Size Preferred = new(1280, 720);

    private CameraDevice? _camera;
    private CameraCaptureSession? _session;
    private ImageReader? _reader;
    private HandlerThread? _background;
    private Handler? _handler;
    private TextureView _preview = null!;

    /// <summary>
    /// Set the moment a code is accepted. Frames keep arriving while the activity winds down, and
    /// without this the second one finishes an activity that is already finishing.
    /// </summary>
    private int _answered;

    /// <summary>Set while a frame is being decoded, so frames arriving meanwhile are dropped
    /// rather than queued behind it.</summary>
    private int _decoding;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _preview = new TextureView(this);
        _preview.SurfaceTextureListener = new SurfaceListener(this);
        SetContentView(_preview);
    }

    protected override void OnPause()
    {
        // Released here rather than in OnDestroy: a camera held by a paused activity is a camera
        // no other app can open, and being backgrounded mid-scan is ordinary.
        Close();
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_preview.IsAvailable && _camera is null)
        {
            Open();
        }
    }

    private void Open()
    {
        var manager = (CameraManager?)GetSystemService(CameraService);
        if (manager is null)
        {
            Give(null);
            return;
        }

        try
        {
            var id = BackCamera(manager) ?? manager.GetCameraIdList().FirstOrDefault();
            if (id is null)
            {
                // A device with no camera at all. Nothing to say beyond going back.
                Give(null);
                return;
            }

            _background = new HandlerThread("banter-scan");
            _background.Start();
            _handler = new Handler(_background.Looper!);

            _reader = ImageReader.NewInstance(Preferred.Width, Preferred.Height, ImageFormatType.Yuv420888, maxImages: 2);
            _reader!.SetOnImageAvailableListener(new FrameListener(this), _handler);

            manager.OpenCamera(id, new DeviceCallback(this), _handler);
        }
        catch (System.Exception ex) when (ex is CameraAccessException or SecurityException or IllegalArgumentException)
        {
            // Revoked permission, a camera in use by something else, a device that lied about
            // having one. None is worth a crash on the way back to a form.
            global::Android.Util.Log.Warn(LogTag, $"scan: {ex.Message}");
            Give(null);
        }
    }

    /// <summary>The back camera, which is the one pointed at the thing being scanned.</summary>
    private static string? BackCamera(CameraManager manager)
    {
        foreach (var id in manager.GetCameraIdList())
        {
            var facing = (int?)(manager.GetCameraCharacteristics(id)
                .Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();
            if (facing == (int)LensFacing.Back)
            {
                return id;
            }
        }

        return null;
    }

    private void Started(CameraDevice camera)
    {
        _camera = camera;

        var texture = _preview.SurfaceTexture;
        texture?.SetDefaultBufferSize(Preferred.Width, Preferred.Height);
        var previewSurface = new Surface(texture!);
        var readerSurface = _reader!.Surface!;

        var request = camera.CreateCaptureRequest(CameraTemplate.Preview);
        request.AddTarget(previewSurface);
        request.AddTarget(readerSurface);

        // Continuous autofocus: a code is read at whatever distance somebody happens to hold the
        // phone, and a fixed focus reads one distance well and every other one not at all.
        request.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.ContinuousPicture);

        // The overload taking a SessionConfiguration replaced this one in API 28, and this head
        // supports 26. Rather than carry both paths for two releases nobody is on, keep the call
        // that works on every version in range — deprecated is not removed, and the day it is,
        // raising SupportedOSPlatformVersion is the honest fix rather than a runtime branch.
#pragma warning disable CA1422
        camera.CreateCaptureSession(
            [previewSurface, readerSurface],
            new SessionCallback(this, request),
            _handler);
#pragma warning restore CA1422
    }

    private void Repeat(CameraCaptureSession session, CaptureRequest.Builder request)
    {
        _session = session;
        try
        {
            session.SetRepeatingRequest(request.Build(), null, _handler);
        }
        catch (CameraAccessException ex)
        {
            global::Android.Util.Log.Warn(LogTag, $"scan: {ex.Message}");
            Give(null);
        }
    }

    /// <summary>
    /// One frame. The Y plane of a YUV_420_888 image is exactly the luminance the decoder wants,
    /// so nothing is converted — but it may be padded, and a row stride wider than the image is
    /// the difference between decoding and never decoding.
    /// </summary>
    private void Frame(ImageReader reader)
    {
        if (Volatile.Read(ref _answered) != 0 || Interlocked.Exchange(ref _decoding, 1) != 0)
        {
            // A decode of a dense code takes longer than the gap between frames. Dropping the ones
            // that arrive meanwhile is the point: queueing them would hold buffers the reader needs
            // back, and there are only ever two.
            return;
        }

        try
        {
            byte[] bytes;
            int width, height, stride;

            var image = reader.AcquireLatestImage();
            if (image is null)
            {
                return;
            }

            try
            {
                var plane = image.GetPlanes()![0];
                var buffer = plane.Buffer!;
                bytes = new byte[buffer.Remaining()];
                buffer.Get(bytes);

                width = image.Width;
                height = image.Height;
                stride = plane.RowStride;
            }
            finally
            {
                // Close, not Dispose. Disposing releases the managed peer; Close is what hands the
                // buffer back to the reader, and without it the third frame throws
                // "maxImages (2) has already been acquired" on the camera's own thread, which is
                // fatal and takes the activity with it.
                image.Close();
                image.Dispose();
            }

            Decode(bytes, width, height, stride);
        }
        finally
        {
            Volatile.Write(ref _decoding, 0);
        }
    }

    /// <summary>The copied frame, once the reader has its buffer back.</summary>
    private void Decode(byte[] bytes, int width, int height, int stride)
    {
        if (stride > width)
        {
            // Repack to exactly width*height. The decoder reads row by row, so padding left in
            // place shifts every row and turns a perfectly good code into noise.
            var packed = new byte[width * height];
            for (var row = 0; row < height; row++)
            {
                Array.Copy(bytes, row * stride, packed, row * width, width);
            }

            bytes = packed;
        }

        var text = QrScan.Read(bytes, width, height);
        if (QrScan.LooksLikeAServer(text))
        {
            Give(text);
        }
    }

    /// <summary>
    /// Answers once. Called from the camera's background thread as well as the UI one, so the
    /// interlock is the thing that makes it once rather than the caller being careful.
    /// </summary>
    private void Give(string? server)
    {
        if (Interlocked.Exchange(ref _answered, 1) != 0)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (server is not null)
            {
                var data = new Intent();
                data.PutExtra(ServerExtra, server);
                SetResult(Result.Ok, data);
            }
            else
            {
                SetResult(Result.Canceled);
            }

            Finish();
        });
    }

    private void Close()
    {
        _session?.Close();
        _session = null;
        _camera?.Close();
        _camera = null;
        _reader?.Close();
        _reader = null;
        _background?.QuitSafely();
        _background = null;
        _handler = null;
    }

    private sealed class SurfaceListener(ScannerActivity owner) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(global::Android.Graphics.SurfaceTexture surface, int width, int height) =>
            owner.Open();

        public bool OnSurfaceTextureDestroyed(global::Android.Graphics.SurfaceTexture surface) => true;

        public void OnSurfaceTextureSizeChanged(global::Android.Graphics.SurfaceTexture surface, int width, int height)
        {
        }

        public void OnSurfaceTextureUpdated(global::Android.Graphics.SurfaceTexture surface)
        {
        }
    }

    private sealed class DeviceCallback(ScannerActivity owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.Started(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            owner.Give(null);
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            global::Android.Util.Log.Warn(LogTag, $"scan: camera error {error}");
            camera.Close();
            owner.Give(null);
        }
    }

    private sealed class SessionCallback(ScannerActivity owner, CaptureRequest.Builder request)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => owner.Repeat(session, request);

        public override void OnConfigureFailed(CameraCaptureSession session) => owner.Give(null);
    }

    private sealed class FrameListener(ScannerActivity owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                owner.Frame(reader);
            }
        }
    }
}
