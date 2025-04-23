using System;
using Xilium.CefGlue;

namespace VL.CEF
{
    public delegate void PaintHandler(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height);

    public delegate void AcceleratedPaintHandler(CefPaintElementType type, CefRectangle[] dirtyRects, CefAcceleratedPaintInfo info);
}
