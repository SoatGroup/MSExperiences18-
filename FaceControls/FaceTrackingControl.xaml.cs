﻿using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace FaceControls
{
    public sealed partial class FaceTrackingControl : UserControl
    {
        private readonly DisplayInformation _displayInformation = DisplayInformation.GetForCurrentView();
        private DisplayOrientations _displayOrientation = DisplayOrientations.Portrait;
        private FaceDetectionEffect _faceDetectionEffect;
        private IMediaEncodingProperties _previewProperties;
        private bool isPreviewing;
        private bool _mirroringPreview = true;
        private BitmapIcon smiley;
        private BitmapIcon smileyNeutral;

        public FaceServiceClient FaceClient;

        //0 for false, 1 for true.
        private static int isCheckingSmile = 0;
        private object isSmilingLock = new object();
        private DateTime? lastSmileCheck;

        public string Status { get; set; }
        public bool IsCheckSmileEnabled { get; set; }
        public MediaCapture MediaCapture { get; private set; }
        /// <summary>
        /// Occurs when a face is detected. See FaceDetectedEventArgs
        /// </summary>
        public event TypedEventHandler<FaceDetectionEffect, FaceDetectedEventArgs> FaceDetected;
        public event EventHandler<Face> SmileDetected;

        public FaceTrackingControl()
        {
            this.InitializeComponent();
            isPreviewing = false;

            smiley = new BitmapIcon();
            smiley.UriSource = new Uri("ms-appx:///Assets/smiley.png");
            smiley.Foreground = new SolidColorBrush(Colors.Yellow);

            smileyNeutral = new BitmapIcon();
            smileyNeutral.UriSource = new Uri("ms-appx:///Assets/smiley_neutral.png");
            smileyNeutral.Foreground = new SolidColorBrush(Colors.Yellow);
        }

        public async Task InitCameraAsync()
        {
            try
            {
                Cleanup();

                Status = "Initializing camera to capture audio and video...";

                // Use default initialization
                MediaCapture = new MediaCapture();
                await MediaCapture.InitializeAsync();

                // Set callbacks for failure and recording limit exceeded
                Status = "Device successfully initialized for video recording!";
                MediaCapture.Failed += new MediaCaptureFailedEventHandler(mediaCapture_Failed);

                // Start Preview                
                PreviewControl.Source = MediaCapture;
                PreviewControl.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                // Also mirror the canvas if the preview is being mirrored
                FacesCanvas.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

                await MediaCapture.StartPreviewAsync();
                _previewProperties = MediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

                // Initialize the preview to the current orientation
                if (_previewProperties != null)
                {
                    _displayOrientation = _displayInformation.CurrentOrientation;
                }
                isPreviewing = true;
                Status = "Camera preview succeeded";

                await InitFaceTrackerAsync();
            }
            catch (Exception ex)
            {
                Status = "Unable to initialize camera for audio/video mode: " + ex.Message;
            }
        }

        public void StartFaceTracking()
        {
            // Start detecting faces
            _faceDetectionEffect.Enabled = true;
        }

        public void StopFaceTracking()
        {
            _faceDetectionEffect.Enabled = false;

            CleanCanvas();
        }

        public void CleanCanvas()
        {
            // Remove any existing rectangles from previous events
            FacesCanvas.Children.Clear();
        }

        /// <summary>
        /// Start Face detection
        /// </summary>
        /// <returns></returns>
        private async Task InitFaceTrackerAsync()
        {
            // Create the definition, which will contain some initialization settings
            var definition = new FaceDetectionEffectDefinition();
            // To ensure preview smoothness, do not delay incoming samples
            definition.SynchronousDetectionEnabled = false;
            // In this scenario, choose detection speed over accuracy
            definition.DetectionMode = FaceDetectionMode.HighPerformance;
            // Add the effect to the preview stream
            _faceDetectionEffect = (FaceDetectionEffect)await MediaCapture.AddVideoEffectAsync(definition, MediaStreamType.VideoPreview);
            // Choose the shortest interval between detection events
            _faceDetectionEffect.DesiredDetectionInterval = TimeSpan.FromMilliseconds(33);
            // Register for face detection events
            _faceDetectionEffect.FaceDetected += FaceDetectionEffect_FaceDetected;

            Status = "Face tracker sucessfully initialized!";
        }

        private async void FaceDetectionEffect_FaceDetected(FaceDetectionEffect sender, FaceDetectedEventArgs args)
        {
            // Ask the UI thread to render the face bounding boxes
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => HighlightDetectedFaces(args.ResultFrame.DetectedFaces));
            FaceDetected?.Invoke(sender, args);

            if (IsCheckSmileEnabled)
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => await CheckSmileAsync());
            }
        }

        private async void mediaCapture_Failed(MediaCapture currentCaptureObject, MediaCaptureFailedEventArgs currentFailure)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    Status = "MediaCaptureFailed: " + currentFailure.Message;
                }
                catch (Exception)
                {
                }
                finally
                {
                    Status += "\nCheck if camera is diconnected. Try re-launching the app";
                }
            });
        }

        private async void Cleanup()
        {
            if (MediaCapture != null)
            {
                // Cleanup MediaCapture object
                if (isPreviewing)
                {
                    await MediaCapture.StopPreviewAsync();
                    PreviewControl.Source = null;
                    isPreviewing = false;
                }
                _faceDetectionEffect = null;
                MediaCapture.Dispose();
                MediaCapture = null;
            }
        }

        #region Face detection helpers

        /// <summary>
        /// Iterates over all detected faces, creating and adding Rectangles to the FacesCanvas as face bounding boxes
        /// </summary>
        /// <param name="faces">The list of detected faces from the FaceDetected event of the effect</param>
        private void HighlightDetectedFaces(IReadOnlyList<DetectedFace> faces)
        {
            CleanCanvas();

            if (faces.Count < 1)
                return;

            var orderedFaces = faces.OrderByDescending(f => f.FaceBox.Height * f.FaceBox.Width).ToList();
            // For each detected face
            for (int i = 0; i < orderedFaces.Count; i++)
            {
                // Face coordinate units are preview resolution pixels, which can be a different scale from our display resolution, so a conversion may be necessary
                Windows.UI.Xaml.Shapes.Rectangle faceBoundingBox = ConvertPreviewToUiRectangle(faces[i].FaceBox);

                if (i != 0)
                {
                    // Set bounding box stroke properties
                    faceBoundingBox.StrokeThickness = 2;
                    // Highlight the first face in the set
                    faceBoundingBox.Stroke = new SolidColorBrush(Colors.Yellow);
                    // Add grid to canvas containing all face UI objects
                    FacesCanvas.Children.Add(faceBoundingBox);
                }
                else
                {
                    var left = Canvas.GetLeft(faceBoundingBox);
                    var top = Canvas.GetTop(faceBoundingBox);
                    var faceIcon = IsCheckSmileEnabled ? smiley : smileyNeutral;
                    Canvas.SetLeft(faceIcon, left - faceBoundingBox.Width / 4);
                    Canvas.SetTop(faceIcon, top - faceBoundingBox.Height / 4);
                    faceIcon.Width = faceBoundingBox.Width * 1.5;
                    faceIcon.Height = faceBoundingBox.Height * 1.5;
                    FacesCanvas.Children.Add(faceIcon);
                }
            }
        }

        private async Task CheckSmileAsync()
        {
            if (IsCheckSmileEnabled != false && isCheckingSmile != 1 && (lastSmileCheck == null || lastSmileCheck < DateTime.Now.AddSeconds(-1)))
            {
                // 0 indicates that the method is not in use.
                if (0 == Interlocked.Exchange(ref isCheckingSmile, 1))
                {
                    lastSmileCheck = DateTime.Now;

                    var requiedFaceAttributes = new FaceAttributeType[] { FaceAttributeType.Smile };
                    var previewProperties = MediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;
                    double scale = 480d / (double)previewProperties.Height;
                    VideoFrame videoFrame = new VideoFrame(BitmapPixelFormat.Bgra8, (int)(previewProperties.Width * scale), 480);
                    using (var frame = await MediaCapture.GetPreviewFrameAsync(videoFrame))
                    {
                        if (frame.SoftwareBitmap != null)
                        {
                            var bitmap = frame.SoftwareBitmap;

                            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
                            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
                            encoder.SetSoftwareBitmap(bitmap);

                            await encoder.FlushAsync();
                            var detect = await FaceClient.DetectAsync(stream.AsStream(), false, false, requiedFaceAttributes);
                            if (detect.Any())
                            {
                                var biggestFace = detect.OrderByDescending(f => f.FaceRectangle.Height * f.FaceRectangle.Width).First();
                                if (biggestFace.FaceAttributes.Smile > 0.5)
                                {
                                    SmileDetected?.Invoke(this, biggestFace);
                                }
                            }
                        }
                    }
                    Interlocked.Exchange(ref isCheckingSmile, 0);
                }
            }
        }

        /// <summary>
        /// Takes face information defined in preview coordinates and returns one in UI coordinates, taking
        /// into account the position and size of the preview control.
        /// </summary>
        /// <param name="faceBoxInPreviewCoordinates">Face coordinates as retried from the FaceBox property of a DetectedFace, in preview coordinates.</param>
        /// <returns>Rectangle in UI (CaptureElement) coordinates, to be used in a Canvas control.</returns>
        private Windows.UI.Xaml.Shapes.Rectangle GetFaceHatRectangle(BitmapBounds faceBoxInPreviewCoordinates)
        {
            var result = new Windows.UI.Xaml.Shapes.Rectangle();
            var previewStream = _previewProperties as VideoEncodingProperties;

            // If there is no available information about the preview, return an empty rectangle, as re-scaling to the screen coordinates will be impossible
            if (previewStream == null) return result;

            // Similarly, if any of the dimensions is zero (which would only happen in an error case) return an empty rectangle
            if (previewStream.Width == 0 || previewStream.Height == 0) return result;

            double streamWidth = previewStream.Width;
            double streamHeight = previewStream.Height;

            // For portrait orientations, the width and height need to be swapped
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamHeight = previewStream.Width;
                streamWidth = previewStream.Height;
            }

            // Get the rectangle that is occupied by the actual video feed
            var previewInUI = GetPreviewStreamRectInControl(previewStream, PreviewControl);

            // Scale the width and height from preview stream coordinates to window coordinates
            result.Width = (faceBoxInPreviewCoordinates.Width / streamWidth) * previewInUI.Width * 2;
            result.Height = (faceBoxInPreviewCoordinates.Height / streamHeight) * previewInUI.Height * 2;

            // Scale the X and Y coordinates from preview stream coordinates to window coordinates
            var x = (faceBoxInPreviewCoordinates.X / streamWidth) * previewInUI.Width;
            var y = (faceBoxInPreviewCoordinates.Y / streamHeight) * previewInUI.Height;
            Canvas.SetLeft(result, x - result.Width / 4);
            Canvas.SetTop(result, y + result.Height / 3);

            return result;
        }

        /// <summary>
        /// Takes face information defined in preview coordinates and returns one in UI coordinates, taking
        /// into account the position and size of the preview control.
        /// </summary>
        /// <param name="faceBoxInPreviewCoordinates">Face coordinates as retried from the FaceBox property of a DetectedFace, in preview coordinates.</param>
        /// <returns>Rectangle in UI (CaptureElement) coordinates, to be used in a Canvas control.</returns>
        private Windows.UI.Xaml.Shapes.Rectangle ConvertPreviewToUiRectangle(BitmapBounds faceBoxInPreviewCoordinates)
        {
            var result = new Windows.UI.Xaml.Shapes.Rectangle();
            var previewStream = _previewProperties as VideoEncodingProperties;

            // If there is no available information about the preview, return an empty rectangle, as re-scaling to the screen coordinates will be impossible
            if (previewStream == null) return result;

            // Similarly, if any of the dimensions is zero (which would only happen in an error case) return an empty rectangle
            if (previewStream.Width == 0 || previewStream.Height == 0) return result;

            double streamWidth = previewStream.Width;
            double streamHeight = previewStream.Height;

            // For portrait orientations, the width and height need to be swapped
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamHeight = previewStream.Width;
                streamWidth = previewStream.Height;
            }

            // Get the rectangle that is occupied by the actual video feed
            var previewInUI = GetPreviewStreamRectInControl(previewStream, PreviewControl);

            // Scale the width and height from preview stream coordinates to window coordinates
            result.Width = (faceBoxInPreviewCoordinates.Width / streamWidth) * previewInUI.Width;
            result.Height = (faceBoxInPreviewCoordinates.Height / streamHeight) * previewInUI.Height;

            // Scale the X and Y coordinates from preview stream coordinates to window coordinates
            var x = (faceBoxInPreviewCoordinates.X / streamWidth) * previewInUI.Width;
            var y = (faceBoxInPreviewCoordinates.Y / streamHeight) * previewInUI.Height;
            Canvas.SetLeft(result, x);
            Canvas.SetTop(result, y);

            return result;
        }

        /// <summary>
        /// Calculates the size and location of the rectangle that contains the preview stream within the preview control, when the scaling mode is Uniform
        /// </summary>
        /// <param name="previewResolution">The resolution at which the preview is running</param>
        /// <param name="previewControl">The control that is displaying the preview using Uniform as the scaling mode</param>
        /// <returns></returns>
        public Rect GetPreviewStreamRectInControl(VideoEncodingProperties previewResolution, CaptureElement previewControl)
        {
            var result = new Rect();

            // In case this function is called before everything is initialized correctly, return an empty result
            if (previewControl == null || previewControl.ActualHeight < 1 || previewControl.ActualWidth < 1 ||
                previewResolution == null || previewResolution.Height == 0 || previewResolution.Width == 0)
            {
                return result;
            }

            var streamWidth = previewResolution.Width;
            var streamHeight = previewResolution.Height;

            //For portrait orientations, the width and height need to be swapped
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamWidth = previewResolution.Height;
                streamHeight = previewResolution.Width;
            }

            // Start by assuming the preview display area in the control spans the entire width and height both (this is corrected in the next if for the necessary dimension)
            result.Width = previewControl.ActualWidth;
            result.Height = previewControl.ActualHeight;

            // If UI is "wider" than preview, letterboxing will be on the sides
            if ((previewControl.ActualWidth / previewControl.ActualHeight > streamWidth / (double)streamHeight))
            {
                var scale = previewControl.ActualHeight / streamHeight;
                var scaledWidth = streamWidth * scale;

                result.X = (previewControl.ActualWidth - scaledWidth) / 2.0;
                result.Width = scaledWidth;
            }
            else // Preview stream is "wider" than UI, so letterboxing will be on the top+bottom
            {
                var scale = previewControl.ActualWidth / streamWidth;
                var scaledHeight = streamHeight * scale;

                result.Y = (previewControl.ActualHeight - scaledHeight) / 2.0;
                result.Height = scaledHeight;
            }

            return result;
        }

        #endregion

        public async Task<Stream> GetCaptureStreamAsync()
        {
            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
            ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => await MediaCapture.CapturePhotoToStreamAsync(imageProperties, stream));
            var stream_send = stream.CloneStream().AsStream();
            return stream_send;
        }

        public async Task GetCaptureFileAsync(StorageFile photoFile)
        {
            ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
            await MediaCapture.CapturePhotoToStorageFileAsync(imageProperties, photoFile);
        }

        public async Task CaptureFaceToFileAsync(StorageFile photoFile, FaceRectangle faceRectangle)
        {
            var previewProperties = MediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;
            double scale = 480d / (double)previewProperties.Height;
            VideoFrame videoFrame = new VideoFrame(BitmapPixelFormat.Bgra8, (int)(previewProperties.Width * scale), 480);
            using (var frame = await MediaCapture.GetPreviewFrameAsync(videoFrame))
            {
                if (frame.SoftwareBitmap != null)
                {
                    var bitmap = frame.SoftwareBitmap;

                    using (IRandomAccessStream writeStream = await photoFile.OpenAsync(FileAccessMode.ReadWrite))
                    {

                        try
                        {
                            // Create an encoder with the desired format
                            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, writeStream);

                            encoder.SetSoftwareBitmap(bitmap);

                            BitmapBounds bounds = new BitmapBounds();
                            bounds.Width = (uint)faceRectangle.Width;
                            bounds.Height = (uint)faceRectangle.Height;
                            bounds.X = (uint)faceRectangle.Left;
                            bounds.Y = (uint)faceRectangle.Top;
                            //Enlarge face rectangle
                            //double width = faceRectangle.Width;
                            //double height = faceRectangle.Height;
                            //double middleFaceX = faceRectangle.Left + width / 2;
                            //double middleFaceY = faceRectangle.Top + height / 2;
                            //width = faceRectangle.Width * 1;//TODO 1.5
                            //height = faceRectangle.Height * 1;//TODO 2
                            //bounds.Width = (uint)width;
                            //bounds.Height = (uint)height;
                            //bounds.X = (uint)(middleFaceX - width / 2);
                            //bounds.Y = (uint)(middleFaceY - height / 2);
                            //TODO prevent out-of-bounds

                            encoder.BitmapTransform.Bounds = bounds;
                            await encoder.FlushAsync();
                        }
                        catch (Exception e)
                        {
                            //TODO handle error
                        }
                    }
                }
            }
        }
    }
}
