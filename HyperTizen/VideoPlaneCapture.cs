using System;
using System.Runtime.InteropServices;

namespace HyperTizen
{
    internal static class VideoPlaneCapture
    {
        private const string LibraryName = "libvideo-capture.so.0.1.0";
        private const string SymbolName = "secvideo_api_capture_video_main";
        private const int PlaneWidth = 960;
        private const int PlaneHeight = 540;
        private const int YBytes = PlaneWidth * PlaneHeight;
        private const int CBytes = YBytes / 2;
        private const int ZonesPerSide = 16;
        private const int ParameterBytes = 256;
        private const int OffRetWidth = 0x08;
        private const int OffRetHeight = 0x0c;
        private const int OffYBuffer = 0x10;
        private const int OffCBuffer = 0x14;
        private const int OffYPitch = 0x2c;
        private const int OffCPitch = 0x30;
        private const int RTLD_NOW = 2;

        [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        private static extern IntPtr OpenLibrary(string fileName, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        private static extern IntPtr FindSymbol(IntPtr library, string symbol);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CaptureVideoMain(int width, int height, IntPtr parameters);

        private static readonly byte[] _yPlane = new byte[YBytes];
        private static readonly byte[] _cPlane = new byte[CBytes];
        private static IntPtr _library;
        private static IntPtr _parameters;
        private static IntPtr _yBuffer;
        private static IntPtr _cBuffer;
        private static CaptureVideoMain _capture;

        public static bool Initialize()
        {
            if (_capture != null)
                return true;

            if (IntPtr.Size != 4)
                return false;

            try
            {
                _library = OpenLibrary(LibraryName, RTLD_NOW);
                if (_library == IntPtr.Zero)
                    _library = OpenLibrary("/usr/lib/" + LibraryName, RTLD_NOW);
                if (_library == IntPtr.Zero)
                    return false;

                IntPtr function = FindSymbol(_library, SymbolName);
                if (function == IntPtr.Zero)
                    return false;

                _parameters = Marshal.AllocHGlobal(ParameterBytes);
                _yBuffer = Marshal.AllocHGlobal(YBytes);
                _cBuffer = Marshal.AllocHGlobal(CBytes);
                if (_parameters == IntPtr.Zero || _yBuffer == IntPtr.Zero || _cBuffer == IntPtr.Zero)
                {
                    ReleaseBuffers();
                    return false;
                }

                for (int offset = 0; offset < ParameterBytes; offset++)
                    Marshal.WriteByte(_parameters, offset, 0);

                Marshal.WriteInt32(_parameters, 0x00, YBytes);
                Marshal.WriteInt32(_parameters, 0x04, CBytes);
                Marshal.WriteInt32(_parameters, OffYBuffer, _yBuffer.ToInt32());
                Marshal.WriteInt32(_parameters, OffCBuffer, _cBuffer.ToInt32());
                _capture = (CaptureVideoMain)Marshal.GetDelegateForFunctionPointer(
                    function, typeof(CaptureVideoMain));
                return true;
            }
            catch
            {
                _capture = null;
                ReleaseBuffers();
                return false;
            }
        }

        public static bool TryGetColors(Color[] colors, out int resultCode)
        {
            resultCode = -1;
            if (_capture == null || colors == null || colors.Length != ZonesPerSide * 4)
                return false;

            try
            {
                resultCode = _capture(PlaneWidth, PlaneHeight, _parameters);
                if (resultCode != 0)
                    return false;

                int width = Marshal.ReadInt32(_parameters, OffRetWidth);
                int height = Marshal.ReadInt32(_parameters, OffRetHeight);
                if (width <= 0) width = PlaneWidth;
                if (height <= 0) height = PlaneHeight;
                if (width < 64 || width > PlaneWidth || height < 64 || height > PlaneHeight)
                    return false;

                int yStride = Marshal.ReadInt32(_parameters, OffYPitch);
                int cStride = Marshal.ReadInt32(_parameters, OffCPitch);
                if (yStride < width || yStride > PlaneWidth) yStride = width;
                if (cStride < width || cStride > PlaneWidth) cStride = width;
                if ((long)yStride * height > YBytes || (long)cStride * ((height + 1) / 2) > CBytes)
                    return false;

                Marshal.Copy(_yBuffer, _yPlane, 0, YBytes);
                Marshal.Copy(_cBuffer, _cPlane, 0, CBytes);

                for (int zone = 0; zone < ZonesPerSide; zone++)
                {
                    int topLeft = zone * width / ZonesPerSide;
                    int topRight = (zone + 1) * width / ZonesPerSide;
                    SampleZone(colors, zone, topLeft, 0, topRight, height * 8 / 100,
                        width, height, yStride, cStride);

                    int rightTop = zone * height / ZonesPerSide;
                    int rightBottom = (zone + 1) * height / ZonesPerSide;
                    SampleZone(colors, ZonesPerSide + zone, width * 92 / 100, rightTop, width, rightBottom,
                        width, height, yStride, cStride);

                    int bottomRight = (ZonesPerSide - zone - 1) * width / ZonesPerSide;
                    int bottomLeft = (ZonesPerSide - zone) * width / ZonesPerSide;
                    SampleZone(colors, ZonesPerSide * 2 + zone, bottomRight, height * 92 / 100,
                        bottomLeft, height, width, height, yStride, cStride);

                    int leftBottom = (ZonesPerSide - zone - 1) * height / ZonesPerSide;
                    int leftTop = (ZonesPerSide - zone) * height / ZonesPerSide;
                    SampleZone(colors, ZonesPerSide * 3 + zone, 0, leftBottom,
                        width * 8 / 100, leftTop, width, height, yStride, cStride);
                }
                return true;
            }
            catch
            {
                resultCode = -100;
                return false;
            }
        }

        private static void SampleZone(Color[] colors, int index, int left, int top, int right, int bottom,
            int width, int height, int yStride, int cStride)
        {
            long yTotal = 0;
            long uTotal = 0;
            long vTotal = 0;
            int samples = 0;

            for (int row = top; row < bottom && row < height; row += 3)
            {
                int yRow = row * yStride;
                int cRow = (row / 2) * cStride;
                for (int column = left; column < right && column < width; column += 3)
                {
                    int yIndex = yRow + column;
                    int cIndex = cRow + (column & ~1);
                    if (yIndex >= _yPlane.Length || cIndex + 1 >= _cPlane.Length)
                        continue;

                    yTotal += _yPlane[yIndex];
                    uTotal += _cPlane[cIndex];
                    vTotal += _cPlane[cIndex + 1];
                    samples++;
                }
            }

            if (samples == 0)
            {
                colors[index] = new Color();
                return;
            }

            int y = (int)(yTotal / samples) - 16;
            int u = (int)(uTotal / samples) - 128;
            int v = (int)(vTotal / samples) - 128;
            if (y < 0) y = 0;

            int red = Clamp8((298 * y + 409 * v + 128) >> 8);
            int green = Clamp8((298 * y - 100 * u - 208 * v + 128) >> 8);
            int blue = Clamp8((298 * y + 516 * u + 128) >> 8);
            colors[index] = new Color { R = red * 4, G = green * 4, B = blue * 4 };
        }

        private static int Clamp8(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return value;
        }

        private static void ReleaseBuffers()
        {
            if (_parameters != IntPtr.Zero) Marshal.FreeHGlobal(_parameters);
            if (_yBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_yBuffer);
            if (_cBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_cBuffer);
            _parameters = IntPtr.Zero;
            _yBuffer = IntPtr.Zero;
            _cBuffer = IntPtr.Zero;
        }
    }
}