using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Editing;
using Windows.Graphics.DirectX.Direct3D11;
using System.Timers;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage;
using System.Threading;
using Windows.Storage.Streams;
using Windows.Media.Transcoding;
using Windows.UI.Core;
using Windows.Media.Capture;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace PresentVideoRecorder.ContentPages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class RecordPage : Page
    {
        public RecordPage()
        {
            this.InitializeComponent();
            this.Setup();
        }

        private async void buttonStart_Click(object sender, RoutedEventArgs e)
        {
            buttonStart.IsEnabled = false;
            buttonStop.IsEnabled = true;
            if (GraphicsCaptureSession.IsSupported())
            {
                await Button_ClickAsync(sender, e);
            }
        }

        public async Task StartCaptureAsync()
        {
            // The GraphicsCapturePicker follows the same pattern the 
            // file pickers do. 
            var picker = new GraphicsCapturePicker();

            GraphicsCaptureItem item = await picker.PickSingleItemAsync();

            // The item may be null if the user dismissed the 
            // control without making a selection or hit Cancel. 
            if (item != null)
            {
                // We'll define this method later in the document.
                StartCaptureInternal(item);
            }
        }

        // Capture API objects.
        private SizeInt32 _lastSize;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;

        // Non-API related members.
        private CanvasDevice _canvasDevice;
        private CompositionGraphicsDevice _compositionGraphicsDevice;
        private Compositor _compositor;
        private CompositionDrawingSurface _surface;

        private void Setup()
        {
            _canvasDevice = new CanvasDevice();
            _compositionGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(Window.Current.Compositor, _canvasDevice);
            _compositor = Window.Current.Compositor;

            _surface = _compositionGraphicsDevice.CreateDrawingSurface(
                new Size(400, 400),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);    // This is the only value that currently works with the composition APIs.

            var visual = _compositor.CreateSpriteVisual();
            visual.RelativeSizeAdjustment = Vector2.One;
            var brush = _compositor.CreateSurfaceBrush(_surface);
            brush.HorizontalAlignmentRatio = 0.5f;
            brush.VerticalAlignmentRatio = 0.5f;
            brush.Stretch = CompositionStretch.Uniform;
            visual.Brush = brush;
            //ElementCompositionPreview.SetElementChildVisual(this, visual);
            ElementCompositionPreview.SetElementChildVisual(visualContainer, visual);
        }

        private void StartCaptureInternal(GraphicsCaptureItem item)
        {
            // Stop the previous capture if we had one.
            StopCapture();

            if (null == captureTimer)
            {
                captureTimer = new System.Timers.Timer(captureInterval);
                captureTimer.Elapsed += CaptureTimer_Elapsed;
            }

            mediaComposition = new MediaComposition();


            _item = item;
            _lastSize = _item.Size;

            _framePool = Direct3D11CaptureFramePool.Create(
               _canvasDevice, // D3D device 
               DirectXPixelFormat.B8G8R8A8UIntNormalized, // Pixel format 
               2, // Number of frames 
               _item.Size); // Size of the buffers 

            //MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            //_mss = mediaComposition.GenerateMediaStreamSource(profile);

            //IntitializeMediaSourceStream(_mss, _item.Size);

            //IntitializeMediaSourceStream((uint)item.Size.Width, (uint)item.Size.Height);

            if (!captureTimer.Enabled)
            {
                captureTimer.Start();
            }

            _framePool.FrameArrived += (s, a) =>
            {
                // The FrameArrived event is raised for every frame on the thread
                // that created the Direct3D11CaptureFramePool. This means we 
                // don't have to do a null-check here, as we know we're the only 
                // one dequeueing frames in our application.  

                // NOTE: Disposing the frame retires it and returns  
                // the buffer to the pool.

                using (var frame = _framePool.TryGetNextFrame())
                {
                    ProcessFrame(frame);
                }
            };

            _item.Closed += (s, a) =>
            {
                StopCapture();
            };

            _session = _framePool.CreateCaptureSession(_item);
            startTime = DateTime.Now;
            _session.StartCapture();
        }

        private void CaptureTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            captureRequested.Set();
        }

        public void StopCapture()
        {
            captureTimer?.Stop();

            _session?.Dispose();
            _framePool?.Dispose();
            _item = null;
            _session = null;
            _framePool = null;
        }

        DateTime startTime;

        System.Timers.Timer captureTimer;

        MediaStreamSample sample;
        //AutoResetEvent sampleArrived = new AutoResetEvent(false);
        ManualResetEvent captureRequested = new ManualResetEvent(false);
        ManualResetEvent sampleArrived = new ManualResetEvent(false);
        int captureInterval = 25; //milliseconds, namely 40 frames per second.

        private bool isPaused = false;

        private async void ProcessFrame(Direct3D11CaptureFrame frame)
        {
            // Resize and device-lost leverage the same function on the
            // Direct3D11CaptureFramePool. Refactoring it this way avoids 
            // throwing in the catch block below (device creation could always 
            // fail) along with ensuring that resize completes successfully and 
            // isn’t vulnerable to device-lost.   
            bool needsReset = false;
            bool recreateDevice = false;

            if ((frame.ContentSize.Width != _lastSize.Width) ||
                (frame.ContentSize.Height != _lastSize.Height))
            {
                needsReset = true;
                _lastSize = frame.ContentSize;
            }


            try
            {
                if (captureRequested.WaitOne(5))
                {
                    // Convert our D3D11 surface into a Win2D object.
                    var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                        _canvasDevice,
                        frame.Surface);

                    byte[] buff = new byte[canvasBitmap.SizeInPixels.Height * canvasBitmap.SizeInPixels.Width * 4];
                    IBuffer pixels = buff.AsBuffer();
                    canvasBitmap.GetPixelBytes(pixels);

                    TimeSpan timestamp = (DateTime.Now - startTime);
                    sample = MediaStreamSample.CreateFromBuffer(pixels, timestamp);
                    sampleArrived.Set();
                    captureRequested.Reset();
                    //sample.Processed += Sample_Processed;

                }
                //else
                //{
                //}

                // Take the D3D11 surface and draw it into a  
                // Composition surface.

                //ID3D11Multithread 

                //// Convert our D3D11 surface into a Win2D object.
                //var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                //    _canvasDevice,
                //    frame.Surface);

                //Second method.
                //if (!isPaused && sampleArrived.WaitOne(5))
                //{
                //    var canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                //    _canvasDevice,
                //    frame.Surface);

                //    //MemoryBuffer buff = new MemoryBuffer(
                //    //    );
                //    byte[] buff = new byte[canvasBitmap.SizeInPixels.Height * canvasBitmap.SizeInPixels.Width * 4];

                //    //Windows.Storage.Streams.Buffer buff
                //    //= new Windows.Storage.Streams.InMemoryRandomAccessStream();
                //    IBuffer pixels = buff.AsBuffer();
                //    canvasBitmap.GetPixelBytes(pixels);

                //    await CreateVideoFromWritableBitmapAsync(pixels, (int)canvasBitmap.SizeInPixels.Width, (int)canvasBitmap.SizeInPixels.Height, TimeSpan.FromMilliseconds(captureInterval), null);
                //    sampleArrived.Reset();

                //    //Helper that handles the drawing for us.
                //    FillSurfaceWithBitmap(canvasBitmap);
                //}
                //Second method.

                //Helper that handles the drawing for us.
                //FillSurfaceWithBitmap(canvasBitmap);
            }

            // This is the device-lost convention for Win2D.
            catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult))
            {
                // We lost our graphics device. Recreate it and reset 
                // our Direct3D11CaptureFramePool.  
                needsReset = true;
                recreateDevice = true;
            }

            if (needsReset)
            {
                ResetFramePool(frame.ContentSize, recreateDevice);
            }
        }

        
        private void Sample_Processed(MediaStreamSample sample, object args)
        {
            //throw new NotImplementedException();
        }

        private void MSS_SampleProcessed(MediaStreamSample sample)
        {
            // Get the surface from the processed sample so that it can be reused 
            IDirect3DSurface surface = sample.Direct3D11Surface;

            // Allow the surface to be reused in the next call to GetSurfaceFromScreenCapture() 
            // (Implementation details not shown) 
        }

        private void FillSurfaceWithBitmap(CanvasBitmap canvasBitmap)
        {
            CanvasComposition.Resize(_surface, canvasBitmap.Size);

            //canvasBitmap.
            using (var session = CanvasComposition.CreateDrawingSession(_surface))
            {
                session.Clear(Colors.Transparent);
                session.DrawImage(canvasBitmap);
            }
        }

        private void ResetFramePool(SizeInt32 size, bool recreateDevice)
        {
            do
            {
                try
                {
                    if (recreateDevice)
                    {
                        _canvasDevice = new CanvasDevice();
                    }

                    _framePool.Recreate(
                        _canvasDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        size);
                }
                // This is the device-lost convention for Win2D.
                catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult))
                {
                    _canvasDevice = null;
                    recreateDevice = true;
                }
            } while (_canvasDevice == null);
        }

        private async Task Button_ClickAsync(object sender, RoutedEventArgs e)
        {
            await StartCaptureAsync();
        }

        private const int c_frameRateN = 40;
        private const int c_frameRateD = 1;

        private DXSurfaceGenerator.SampleGenerator _sampleGenerator = null;

        private void IntitializeMediaSourceStream(uint width = 1920, uint height = 1080)
        {
            //int iWidth = (int)Window.Current.Bounds.Width;
            //int iHeight = (int)Window.Current.Bounds.Height;
            uint iWidth = 1920;
            uint iHeight = 1080;

            iWidth = width;
            iHeight = height;

            MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

            //VideoEncodingProperties videoEncodingProps = profile.Video; 

            VideoEncodingProperties videoEncodingProps = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.H264, iWidth, iHeight);

            videoDesc = new VideoStreamDescriptor(videoEncodingProps);

            videoDesc.EncodingProperties.FrameRate.Numerator = c_frameRateN;
            videoDesc.EncodingProperties.FrameRate.Denominator = c_frameRateD;
            videoDesc.EncodingProperties.Bitrate = (uint)(c_frameRateN * c_frameRateD * iWidth * iHeight * 4);

            appVideoStream = new MediaStreamSource(videoDesc)
            {
                BufferTime = TimeSpan.FromSeconds(60)
            };
            appVideoStream.Starting += AppVideoStream_Starting;
            appVideoStream.SampleRequested += AppVideoStream_SampleRequested;
            appVideoStream.Closed += AppVideoStream_Closed;

            //mediaPlayer.AutoPlay = true;
            //mediaPlayer.CurrentStateChanged += mediaPlayer_CurrentStateChanged;
            //mediaPlayer.SetMediaStreamSource(appVideoStream);
            //_hasSetMediaSource = true;

            //_sampleGenerator = new DXSurfaceGenerator.SampleGenerator();
        }

        private bool _hasSetMediaSource;
        private bool _mediaSourceIsLoaded;
        private bool _playRequestPending;

        void mediaPlayer_CurrentStateChanged(object sender, RoutedEventArgs e)
        {
            //switch (mediaPlayer.CurrentState)
            //{
            //    case MediaElementState.Paused:
            //    case MediaElementState.Stopped:
            //        _mediaSourceIsLoaded = true;
            //        if (_playRequestPending)
            //        {
            //            mediaPlayer.Play();
            //            _playRequestPending = false;
            //        }
            //        break;
            //}
        }

        private void IntitializeMediaSourceStream(MediaStreamSource mss, SizeInt32? frameSize)
        {

            appVideoStream = mss;
            appVideoStream.BufferTime = TimeSpan.FromSeconds(10);
            appVideoStream.Starting += AppVideoStream_Starting;
            appVideoStream.SampleRequested += AppVideoStream_SampleRequested;
            appVideoStream.Closed += AppVideoStream_Closed;

            //_sampleGenerator = new DXSurfaceGenerator.SampleGenerator();
            //mediaPlayer.AutoPlay = true;
            //mediaPlayer.CurrentStateChanged += mediaPlayer_CurrentStateChanged;
            //mediaPlayer.SetMediaStreamSource(appVideoStream);
            //_hasSetMediaSource = true;
        }

        private async void AppVideoStream_Closed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
        {

            //await SaveFile();
            //MediaStreamSour
        }

        VideoStreamDescriptor videoDesc;
        MediaStreamSource appVideoStream;

        private void AppVideoStream_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {

            if (captureRequested.WaitOne(5))
            {
                //_sampleGenerator.GenerateSample(args.Request);
                args.Request.Sample = sample;
                captureRequested.Reset();

            }

        }

        private void AppVideoStream_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            //_sampleGenerator.Initialize(appVideoStream, videoDesc);
            args.Request.SetActualStartPosition(new TimeSpan(0));
        }

        MediaComposition mediaComposition;

        private async Task CreateVideoFromWritableBitmapAsync(IBuffer bitmap,
            int widthInPixels, int heightInPixels,
            TimeSpan originalDuration,
            List<WriteableBitmap> WBs)
        {
            var bb = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(),
                bitmap, widthInPixels, heightInPixels, DirectXPixelFormat.B8G8R8A8UIntNormalized);

            MediaClip mediaClip = MediaClip.CreateFromSurface(bb, originalDuration);


            mediaComposition.Clips.Add(mediaClip);
            mediaComposition.SaveAsync()
        }

        const string DESKTOP_VIDEO_FILE_NAME_PREFIX = "DesktopCaptureVideo";

        private async Task SaveFile(string fileName = null, StorageFolder folder = null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                string fileNameSuffix = DateTime.Now.ToString("yyyyMMdd_HH_mm_ss_fff");
                fileName = $"DesktopCaptureVideo_{fileNameSuffix}.mp4";
            }

            
            StorageFile file;
            if (null == folder)
            {
                folder = Windows.Storage.KnownFolders.VideosLibrary;
                
                file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            }
            else
            {
                file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            }

            await mediaComposition.SaveAsync(file);
            mediaComposition = await MediaComposition.LoadAsync(file);
            var saveOperation = mediaComposition.RenderToFileAsync(file);

            //saveOperation.Progress = new AsyncOperationProgressHandler<TranscodeFailureReason, double>(async (info, progress) =>
            //{
            //    await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, new DispatchedHandler(() =>
            //    {
            //        //ShowErrorMessage(string.Format("Saving file... Progress: {0:F0}%", progress));
            //    }));
            //});
            //saveOperation.Completed = new AsyncOperationWithProgressCompletedHandler<TranscodeFailureReason, double>(async (info, status) =>
            //{
            //    await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, new DispatchedHandler(() =>
            //    {
            //        try
            //        {
            //            var results = info.GetResults();
            //            if (results != TranscodeFailureReason.None || status != AsyncStatus.Completed)
            //            {
            //                //MessageBox.("Saving was unsuccessful");
            //            }
            //            else
            //            {
            //                //ShowErrorMessage("Trimmed clip saved to file");
            //            }
            //        }
            //        finally
            //        {
            //            // Update UI whether the operation succeeded or not
            //        }

            //    }));
            //});

            await saveOperation;
        }

        private async void buttonStop_Click(object sender, RoutedEventArgs e)
        {
            buttonStop.IsEnabled = false;
            StopCapture();
            await SaveFile();
            buttonStart.IsEnabled = true;
        }

        private void buttonPause_Click(object sender, RoutedEventArgs e)
        {
            this.isPaused = !this.isPaused;
            buttonPauseText.Text = this.isPaused ? "继续录像" : "暂停录像";
        }
    }
}
