using SkiaSharp;
using Stride.Core.Mathematics;
using System;
using VL.Core;
using VL.Skia;
using Xilium.CefGlue;

namespace VL.CEF
{
    public sealed partial class SkiaWebRenderer : WebRenderer, ILayer
    {
        private readonly object syncRoot = new object();

        public SkiaWebRenderer(NodeContext nodeContext) : base(nodeContext, sharedTextureEnabled: false)
        {
        }

        public ILayer Update()
        {
            FBrowserHost.SendExternalBeginFrame();
            return this;
        }

        public override void Dispose()
        {
            base.Dispose();
            FImage?.Dispose();
        }

        protected override void OnPaintCore(CefBrowser browser, CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()), buffer, width * 4);
            lock (syncRoot)
            {
                FImage?.Dispose();
                FImage = image;
            }
        }
        private SKImage FImage;

        public void Render(CallerInfo caller)
        {
            lock (syncRoot)
            {
                if (FImage is null)
                    return;

                var canvas = caller.Canvas;
                if (canvas is null)
                    return;

                var localClipBounds = canvas.LocalClipBounds;
                var deviceClipBounds = canvas.DeviceClipBounds;

                RectangleF bounds;
                switch (SizeMode)
                {
                    case SizeMode.RenderView:
                        bounds = new RectangleF(localClipBounds.Left, localClipBounds.Top, localClipBounds.Width, localClipBounds.Height);
                        break;
                    case SizeMode.Custom:
                        bounds = Bounds;
                        break;
                    default:
                        throw new NotImplementedException();
                }

                // Ensure we render in the proper size
                var v = canvas.TotalMatrix.MapVector(bounds.Width, bounds.Height);
                Size = new Vector2(v.X, v.Y);

                caller.Canvas.DrawImage(FImage, new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
            }
        }
    }
}
