using System;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;
using Tizen.Applications;
using Tizen.Applications.Notifications;

// Modified on 2026-10-06; capture API handling adapted from tizen-os-ambilight.
namespace HyperTizen
{
    public static class Capturer
    {
        private const int ZonesPerSide = 16;
        private const int OutputZoneCount = ZonesPerSide * 4;
        private const int SamplerSpotCount = 24;
        private static Condition _condition;
        private static IntPtr _library;
        private static GetConditionFunc _getCondition;
        private static SetPositionFunc _setPosition;
        private static GetPixelFunc _getPixel;
        private static Color[] _lastColors = new Color[OutputZoneCount];
        private static Color[] _filteredSamplerColors = new Color[SamplerSpotCount];
        private static bool[] _hasSamplerColor = new bool[SamplerSpotCount];
        private static DateTime[] _lastSamplerUpdateUtc = new DateTime[SamplerSpotCount];
        private static readonly object _samplerLock = new object();
        private static Thread _samplerThread;
        private static volatile bool _samplerThreadRequested;
        private static int _samplerCursor;
        private static bool _videoPlaneAvailable;
        private static bool _videoPlaneDisabled;
        private static int _videoPlaneDrmFailures;
        private static DateTime _nextVideoPlaneProbeUtc;
        private static CaptureMethod _activeCaptureMethod = CaptureMethod.PixelSampler;
        private static readonly object _imageLock = new object();
        private static readonly SKBitmap _image = new SKBitmap(64, 48);
        private static readonly SKCanvas _canvas = new SKCanvas(_image);
        private static readonly SKPaint _paint = new SKPaint();
        private static readonly Color[] _lastImageColors = new Color[OutputZoneCount];
        private static string _cachedImage;
        private static bool _hasCachedImage;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCondition
        {
            public int ScreenCapturePoints;
            public int PixelDensityX;
            public int PixelDensityY;
            public int SleepMS;
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeColor
        {
            public int R;
            public int G;
            public int B;
            public int LowInputLagOnOff;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetConditionFunc(out NativeCondition condition);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetPositionFunc(int index, int x, int y);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetPixelFunc(int index, out NativeColor color);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        private const int RTLD_NOW = 2;
        private const double SamplerSmoothingTimeMs = 180.0;

        private enum CaptureMethod
        {
            PixelSampler,
            VideoPlane
        }

        private static CapturePoint[] _capturedPoints = new CapturePoint[] {
            new CapturePoint(0.0625, 0.05),
            new CapturePoint(0.1875, 0.05),
            new CapturePoint(0.3125, 0.05),
            new CapturePoint(0.4375, 0.05),
            new CapturePoint(0.5625, 0.05),
            new CapturePoint(0.6875, 0.05),
            new CapturePoint(0.8125, 0.05),
            new CapturePoint(0.9375, 0.05),
            new CapturePoint(0.95, 0.125),
            new CapturePoint(0.95, 0.375),
            new CapturePoint(0.95, 0.625),
            new CapturePoint(0.95, 0.875),
            new CapturePoint(0.9375, 0.95),
            new CapturePoint(0.8125, 0.95),
            new CapturePoint(0.6875, 0.95),
            new CapturePoint(0.5625, 0.95),
            new CapturePoint(0.4375, 0.95),
            new CapturePoint(0.3125, 0.95),
            new CapturePoint(0.1875, 0.95),
            new CapturePoint(0.0625, 0.95),
            new CapturePoint(0.05, 0.875),
            new CapturePoint(0.05, 0.625),
            new CapturePoint(0.05, 0.375),
            new CapturePoint(0.05, 0.125)
        };

        public static bool GetCondition()
        {
            try
            {
                if (_library == IntPtr.Zero)
                    _library = dlopen("libvideoenhance.so", RTLD_NOW);

                if (_library == IntPtr.Zero)
                    return ShowUnsupportedNotification();

                _getCondition = _getCondition ?? GetFunction<GetConditionFunc>(
                    "ppi_ve_get_rgb_measure_condition", "ve_get_rgb_measure_condition", "cs_ve_get_rgb_measure_condition");
                _setPosition = _setPosition ?? GetFunction<SetPositionFunc>(
                    "ppi_ve_set_rgb_measure_position", "ve_set_rgb_measure_position", "cs_ve_set_rgb_measure_position");
                _getPixel = _getPixel ?? GetFunction<GetPixelFunc>(
                    "ppi_ve_get_rgb_measure_pixel", "ve_get_rgb_measure_pixel", "cs_ve_get_rgb_measure_pixel");

                if (_library == IntPtr.Zero || _getCondition == null || _setPosition == null || _getPixel == null)
                    return ShowUnsupportedNotification();

                NativeCondition nativeCondition;
                if (_getCondition(out nativeCondition) < 0 ||
                    nativeCondition.ScreenCapturePoints < 1 ||
                    nativeCondition.PixelDensityX < 1 || nativeCondition.PixelDensityY < 1 ||
                    nativeCondition.Width < nativeCondition.PixelDensityX ||
                    nativeCondition.Height < nativeCondition.PixelDensityY)
                    return false;

                _condition = new Condition
                {
                    ScreenCapturePoints = nativeCondition.ScreenCapturePoints,
                    PixelDensityX = nativeCondition.PixelDensityX,
                    PixelDensityY = nativeCondition.PixelDensityY,
                    SleepMS = nativeCondition.SleepMS,
                    Width = nativeCondition.Width,
                    Height = nativeCondition.Height
                };
                _videoPlaneAvailable = VideoPlaneCapture.Initialize();
                return true;
            }
            catch
            {
                return ShowUnsupportedNotification();
            }
        }

        private static bool ShowUnsupportedNotification()
        {
            Notification notification = new Notification
            {
                Title = "HyperTizen",
                Content = "Your TV does not support the required functions for HyperTizen.",
                Count = 1
            };

            NotificationManager.Post(notification);
            return false;
        }

        private static T GetFunction<T>(params string[] names) where T : Delegate
        {
            foreach (string name in names)
            {
                IntPtr address = dlsym(_library, name);
                if (address != IntPtr.Zero)
                    return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }

            return null;
        }

        public static void SetCapturePoints(CapturePoint[] capturePoints)
        {
            if (capturePoints == null || capturePoints.Length != SamplerSpotCount)
                throw new ArgumentException("The perimeter mapper requires exactly 24 sampler points.");

            lock (_samplerLock)
            {
                _capturedPoints = capturePoints;
                _lastColors = new Color[OutputZoneCount];
                _filteredSamplerColors = new Color[capturePoints.Length];
                _hasSamplerColor = new bool[capturePoints.Length];
                _lastSamplerUpdateUtc = new DateTime[capturePoints.Length];
                _samplerCursor = 0;
                _hasCachedImage = false;
            }
        }

        public static Color[] GetColors()
        {
            if (_videoPlaneAvailable && !_videoPlaneDisabled && DateTime.UtcNow >= _nextVideoPlaneProbeUtc)
            {
                Color[] videoColors = new Color[OutputZoneCount];
                int resultCode;
                if (VideoPlaneCapture.TryGetColors(videoColors, out resultCode))
                {
                    _videoPlaneDrmFailures = 0;
                    _nextVideoPlaneProbeUtc = DateTime.MinValue;
                    Array.Copy(videoColors, _lastColors, _lastColors.Length);
                    SetCaptureMethod(CaptureMethod.VideoPlane);
                    return videoColors;
                }

                if (resultCode == -4)
                {
                    _videoPlaneDrmFailures++;
                    if (_videoPlaneDrmFailures >= 3)
                    {
                        _nextVideoPlaneProbeUtc = DateTime.UtcNow.AddMilliseconds(500);
                        SetCaptureMethod(CaptureMethod.PixelSampler);
                    }
                    else if (_activeCaptureMethod == CaptureMethod.VideoPlane)
                    {
                        return (Color[])_lastColors.Clone();
                    }
                }
                else if (resultCode == -2 || resultCode == 0)
                {
                    _videoPlaneDisabled = true;
                    SetCaptureMethod(CaptureMethod.PixelSampler);
                }
                else
                {
                    _nextVideoPlaneProbeUtc = DateTime.UtcNow.AddMilliseconds(100);
                    if (_activeCaptureMethod == CaptureMethod.VideoPlane)
                        return (Color[])_lastColors.Clone();
                }
            }

            return GetPixelSamplerColors();
        }

        private static Color[] GetPixelSamplerColors()
        {
            EnsureSamplerThread();
            Color[] measuredColors = new Color[SamplerSpotCount];
            bool[] hasColor = new bool[SamplerSpotCount];
            lock (_samplerLock)
            {
                Array.Copy(_filteredSamplerColors, measuredColors, SamplerSpotCount);
                Array.Copy(_hasSamplerColor, hasColor, SamplerSpotCount);
            }

            int measured = 0;
            for (int i = 0; i < hasColor.Length; i++)
                if (hasColor[i]) measured++;

            if (measured == 0)
                return (Color[])_lastColors.Clone();

            Color[] colors = InterpolateSamplerRing(measuredColors, hasColor);
            Array.Copy(colors, _lastColors, OutputZoneCount);
            return colors;
        }

        private static void EnsureSamplerThread()
        {
            _samplerThreadRequested = true;
            lock (_samplerLock)
            {
                if (_samplerThread != null && _samplerThread.IsAlive)
                    return;

                _samplerThread = new Thread(PixelSamplerLoop)
                {
                    IsBackground = true,
                    Name = "HyperTizen-ppi-sampler"
                };
                _samplerThread.Start();
            }
        }

        private static void PixelSamplerLoop()
        {
            while (true)
            {
                if (!_samplerThreadRequested)
                {
                    Thread.Sleep(10);
                    continue;
                }

                int slotCount = Math.Min(_condition.ScreenCapturePoints, _capturedPoints.Length);
                if (slotCount < 1 || _setPosition == null || _getPixel == null)
                {
                    Thread.Sleep(100);
                    continue;
                }

                int start = _samplerCursor;
                for (int slot = 0; slot < slotCount; slot++)
                {
                    int spotIndex = (start + slot) % _capturedPoints.Length;
                    CapturePoint point = _capturedPoints[spotIndex];
                    int x = (int)(point.X * _condition.Width) - _condition.PixelDensityX / 2;
                    int y = (int)(point.Y * _condition.Height) - _condition.PixelDensityY / 2;
                    x = Clamp(x, 0, _condition.Width - _condition.PixelDensityX);
                    y = Clamp(y, 0, _condition.Height - _condition.PixelDensityY);
                    _setPosition(slot, x, y);
                }

                Thread.Sleep(Math.Max(1, _condition.SleepMS));
                DateTime now = DateTime.UtcNow;
                lock (_samplerLock)
                {
                    for (int slot = 0; slot < slotCount; slot++)
                    {
                        int spotIndex = (start + slot) % _capturedPoints.Length;
                        NativeColor nativeColor;
                        int result = _getPixel(slot, out nativeColor);
                        if (result < 0 || nativeColor.R < 0 || nativeColor.R > 1023 ||
                            nativeColor.G < 0 || nativeColor.G > 1023 ||
                            nativeColor.B < 0 || nativeColor.B > 1023)
                            continue;

                        Color raw = new Color
                        {
                            R = nativeColor.R,
                            G = nativeColor.G,
                            B = nativeColor.B
                        };
                        double elapsedMs = _hasSamplerColor[spotIndex]
                            ? (now - _lastSamplerUpdateUtc[spotIndex]).TotalMilliseconds
                            : SamplerSmoothingTimeMs;
                        if (elapsedMs <= 0 || elapsedMs > 1000)
                            elapsedMs = SamplerSmoothingTimeMs;
                        double alpha = 1.0 - Math.Exp(-elapsedMs / SamplerSmoothingTimeMs);
                        Color previous = _filteredSamplerColors[spotIndex];
                        _filteredSamplerColors[spotIndex] = new Color
                        {
                            R = (int)Math.Round(previous.R + (raw.R - previous.R) * alpha),
                            G = (int)Math.Round(previous.G + (raw.G - previous.G) * alpha),
                            B = (int)Math.Round(previous.B + (raw.B - previous.B) * alpha)
                        };
                        _hasSamplerColor[spotIndex] = true;
                        _lastSamplerUpdateUtc[spotIndex] = now;
                    }
                }

                _samplerCursor = (start + slotCount) % _capturedPoints.Length;
            }
        }

        private static void SetCaptureMethod(CaptureMethod method)
        {
            if (_activeCaptureMethod == method)
                return;

            if (method == CaptureMethod.PixelSampler)
            {
                SeedSamplerFromOutput(_lastColors);
            }
            else
                _samplerThreadRequested = false;

            _activeCaptureMethod = method;
            NotificationManager.Post(new Notification
            {
                Title = "HyperTizen",
                Content = method == CaptureMethod.VideoPlane
                    ? "Screen capture switched to full-frame video capture."
                    : "Screen capture switched to the pixel-sampling fallback.",
                Count = 1
            });
        }

        private static Color[] InterpolateSamplerRing(Color[] samples, bool[] hasColor)
        {
            Color[] colors = new Color[OutputZoneCount];
            for (int zone = 0; zone < OutputZoneCount; zone++)
            {
                double position = zone + 0.5;
                int previousIndex = -1;
                int nextIndex = -1;
                double previousPosition = double.NegativeInfinity;
                double nextPosition = double.PositiveInfinity;

                for (int spot = 0; spot < SamplerSpotCount; spot++)
                {
                    if (!hasColor[spot])
                        continue;

                    double spotPosition = SamplerRingPosition(spot);
                    if (spotPosition <= position && spotPosition > previousPosition)
                    {
                        previousPosition = spotPosition;
                        previousIndex = spot;
                    }
                    if (spotPosition > position && spotPosition < nextPosition)
                    {
                        nextPosition = spotPosition;
                        nextIndex = spot;
                    }
                }

                if (previousIndex < 0)
                {
                    previousIndex = FindLastMeasuredSpot(hasColor);
                    previousPosition = SamplerRingPosition(previousIndex) - OutputZoneCount;
                }
                if (nextIndex < 0)
                {
                    nextIndex = FindFirstMeasuredSpot(hasColor);
                    nextPosition = SamplerRingPosition(nextIndex) + OutputZoneCount;
                }

                if (previousIndex == nextIndex)
                    colors[zone] = samples[previousIndex];
                else
                {
                    double amount = (position - previousPosition) / (nextPosition - previousPosition);
                    colors[zone] = Interpolate(samples[previousIndex], samples[nextIndex], amount);
                }
            }
            return colors;
        }

        private static int FindFirstMeasuredSpot(bool[] hasColor)
        {
            for (int i = 0; i < hasColor.Length; i++)
                if (hasColor[i]) return i;
            return 0;
        }

        private static int FindLastMeasuredSpot(bool[] hasColor)
        {
            for (int i = hasColor.Length - 1; i >= 0; i--)
                if (hasColor[i]) return i;
            return 0;
        }

        private static double SamplerRingPosition(int spot)
        {
            if (spot < 8)
                return ((spot + 0.5) / 8.0) * ZonesPerSide;
            if (spot < 12)
                return ZonesPerSide + (((spot - 8) + 0.5) / 4.0) * ZonesPerSide;
            if (spot < 20)
                return ZonesPerSide * 2 + (((spot - 12) + 0.5) / 8.0) * ZonesPerSide;
            return ZonesPerSide * 3 + (((spot - 20) + 0.5) / 4.0) * ZonesPerSide;
        }

        private static void SeedSamplerFromOutput(Color[] colors)
        {
            lock (_samplerLock)
            {
                for (int spot = 0; spot < SamplerSpotCount; spot++)
                {
                    double position = SamplerRingPosition(spot);
                    int firstZone = (int)Math.Floor(position) % OutputZoneCount;
                    double amount = position - Math.Floor(position);
                    Color seeded = Interpolate(colors[firstZone], colors[(firstZone + 1) % OutputZoneCount], amount);
                    _filteredSamplerColors[spot] = seeded;
                    _hasSamplerColor[spot] = true;
                    _lastSamplerUpdateUtc[spot] = DateTime.UtcNow;
                }
            }
        }

        private static Color Interpolate(Color first, Color second, double amount)
        {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            return new Color
            {
                R = (int)Math.Round(first.R + (second.R - first.R) * amount),
                G = (int)Math.Round(first.G + (second.G - first.G) * amount),
                B = (int)Math.Round(first.B + (second.B - first.B) * amount)
            };
        }

        public static string ToImage(Color[] colors)
        {
            lock (_imageLock)
            {
                if (_hasCachedImage && HasSameColors(colors, _lastImageColors))
                    return _cachedImage;

                _canvas.Clear(SKColors.Transparent);
                for (int zone = 0; zone < ZonesPerSide; zone++)
                {
                    _paint.Color = ClampColor(colors[zone]);
                    _canvas.DrawRect(zone * 4, 0, 4, 4, _paint);

                    _paint.Color = ClampColor(colors[ZonesPerSide + zone]);
                    _canvas.DrawRect(61, zone * 3, 3, 3, _paint);

                    _paint.Color = ClampColor(colors[ZonesPerSide * 2 + zone]);
                    _canvas.DrawRect((ZonesPerSide - zone - 1) * 4, 44, 4, 4, _paint);

                    _paint.Color = ClampColor(colors[ZonesPerSide * 3 + zone]);
                    _canvas.DrawRect(0, (ZonesPerSide - zone - 1) * 3, 3, 3, _paint);
                }

                _canvas.Flush();
                using (var skImage = SKImage.FromBitmap(_image))
                using (var data = skImage.Encode(SKEncodedImageFormat.Png, 100))
                {
                    _cachedImage = Convert.ToBase64String(data.ToArray());
                    Array.Copy(colors, _lastImageColors, _lastImageColors.Length);
                    _hasCachedImage = true;
                    return _cachedImage;
                }
            }
        }

        private static bool HasSameColors(Color[] colors, Color[] previousColors)
        {
            if (colors.Length != previousColors.Length)
                return false;

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i].R != previousColors[i].R ||
                    colors[i].G != previousColors[i].G ||
                    colors[i].B != previousColors[i].B)
                    return false;
            }

            return true;
        }

        static SKColor ClampColor(Color color)
        {
            byte r = (byte)(Clamp(color.R, 0, 1023) >> 2);
            byte g = (byte)(Clamp(color.G, 0, 1023) >> 2);
            byte b = (byte)(Clamp(color.B, 0, 1023) >> 2);
            return new SKColor(r, g, b);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    public struct Color
    {
        public int R;
        public int G;
        public int B;
    }

    public struct Condition
    {
        public int ScreenCapturePoints;

        public int PixelDensityX;

        public int PixelDensityY;

        public int SleepMS;

        public int Width;

        public int Height;
    }

    public struct CapturePoint
    {
        public CapturePoint(double x, double y)
        {
            this.X = x;
            this.Y = y;
        }

        public double X;
        public double Y;
    }
}
