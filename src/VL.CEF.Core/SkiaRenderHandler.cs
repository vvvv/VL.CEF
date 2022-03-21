using SharpDX.Direct3D11;
using SkiaSharp;
using System;
using VL.Skia;
using Xilium.CefGlue;
using Vector2 = Stride.Core.Mathematics.Vector2;
using System.Collections.Generic;
using System.Linq;
using VL.Skia.Egl;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using System.Threading;

namespace VL.CEF
{
    public sealed partial class SkiaRenderHandler : IRenderHandler, ILayer
    {
        private readonly object syncRoot = new object();

        private readonly RenderContext renderContext;
        private readonly Device device;
        private SKImage rasterImage, textureImage;
        private Texture2D sharedTexture;
        private IntPtr sharedHandle;
        private WebRenderer webRenderer;

        public SkiaRenderHandler()
        {
            renderContext = RenderContext.ForCurrentThread();
            if (renderContext.EglContext.Dislpay.TryGetD3D11Device(out var d3dDevice))
            {
                device = new Device(d3dDevice);
            }
        }

        public void Initialize(WebRenderer webRenderer)
        {
            this.webRenderer = webRenderer;
        }

        public void Dispose()
        {
            rasterImage?.Dispose();
            textureImage?.Dispose();
            sharedTexture?.Dispose();
            renderContext.Dispose();
        }

        public void OnPaint(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()), buffer, width * 4);
            lock (syncRoot)
            {
                rasterImage?.Dispose();
                rasterImage = image;
            }
        }

        public void OnAcceleratedPaint(CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle)
        {
            lock (syncRoot)
            {
                if (sharedHandle != this.sharedHandle)
                {
                    this.sharedHandle = sharedHandle;
                    sharedTexture?.Dispose();
                    sharedTexture = device?.OpenSharedResource<Texture2D>(sharedHandle);
                }
            }
        }

        public void Render(CallerInfo caller)
        {
            var canvas = caller.Canvas;
            var localClipBounds = canvas.LocalClipBounds;
            var deviceClipBounds = canvas.DeviceClipBounds;

            // Ensure we render in the proper size
            webRenderer.Size = new Vector2(deviceClipBounds.Width, deviceClipBounds.Height);

            lock (syncRoot)
            {
                // Transfer ownership
                var texture = Interlocked.Exchange(ref sharedTexture, null);
                if (texture != null)
                {
                    textureImage?.Dispose();
                    textureImage = ToImage(texture);
                }

                var image = textureImage ?? rasterImage;
                if (image != null)
                    canvas.DrawImage(image, localClipBounds);
            }
        }

        SKImage ToImage(Texture2D texture)
        {    
            // Taken form VL.Video.MediaFoundation
            const int GL_TEXTURE_BINDING_2D = 0x8069;

            var eglContext = renderContext.EglContext;
            var eglImage = eglContext.CreateImageFromD3D11Texture(texture.NativePointer);

            uint textureId = 0;
            NativeGles.glGenTextures(1, ref textureId);

            // We need to restore the currently bound texture (https://github.com/devvvvs/vvvv/issues/5925)
            NativeGles.glGetIntegerv(GL_TEXTURE_BINDING_2D, out var currentTextureId);
            NativeGles.glBindTexture(NativeGles.GL_TEXTURE_2D, textureId);
            NativeGles.glEGLImageTargetTexture2DOES(NativeGles.GL_TEXTURE_2D, eglImage);
            NativeGles.glBindTexture(NativeGles.GL_TEXTURE_2D, (uint)currentTextureId);

            var description = texture.Description;
            var colorType = GetColorType(description.Format);
            var glInfo = new GRGlTextureInfo(
                id: textureId,
                target: NativeGles.GL_TEXTURE_2D,
                format: colorType.ToGlSizedFormat());

            var backendTexture = new GRBackendTexture(
                width: description.Width,
                height: description.Height,
                mipmapped: false,
                glInfo: glInfo);

            var image = SKImage.FromTexture(
                renderContext.SkiaContext,
                backendTexture,
                GRSurfaceOrigin.BottomLeft,
                colorType,
                SKAlphaType.Premul,
                colorspace: SKColorSpace.CreateSrgb(),
                releaseProc: _ =>
                {
                    renderContext.MakeCurrent();

                    backendTexture.Dispose();
                    NativeGles.glDeleteTextures(1, ref textureId);
                    eglImage.Dispose();
                    texture.Dispose();
                });

            return image;
        }

        static SKColorType GetColorType(Format format)
        {
            switch (format)
            {
                case Format.B5G6R5_UNorm:
                    return SKColorType.Rgb565;
                case Format.B8G8R8A8_UNorm:
                case Format.B8G8R8A8_UNorm_SRgb:
                    return SKColorType.Bgra8888;
                case Format.R8G8B8A8_UNorm:
                case Format.R8G8B8A8_UNorm_SRgb:
                    return SKColorType.Rgba8888;
                case Format.R10G10B10A2_UNorm:
                    return SKColorType.Rgba1010102;
                case Format.R16G16B16A16_Float:
                    return SKColorType.RgbaF16;
                case Format.R16G16B16A16_UNorm:
                    return SKColorType.Rgba16161616;
                case Format.R32G32B32A32_Float:
                    return SKColorType.RgbaF32;
                case Format.R16G16_Float:
                    return SKColorType.RgF16;
                case Format.R16G16_UNorm:
                    return SKColorType.Rg1616;
                case Format.R8G8_UNorm:
                    return SKColorType.Rg88;
                case Format.A8_UNorm:
                    return SKColorType.Alpha8;
                case Format.R8_UNorm:
                    return SKColorType.Gray8;
                default:
                    return SKColorType.Unknown;
            }
        }
    }
}
