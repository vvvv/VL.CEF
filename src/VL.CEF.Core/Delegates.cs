using System;
using Xilium.CefGlue;

namespace VL.CEF
{
    internal delegate void PaintHandler(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height);

    internal delegate void AcceleratedPaintHandler(CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle);

}
