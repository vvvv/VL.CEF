using SharpDX.Direct3D11;
using SkiaSharp;
using System;
using VL.Skia;
using Xilium.CefGlue;
using Vector2 = Stride.Core.Mathematics.Vector2;
using VL.Skia.Egl;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Device1 = SharpDX.Direct3D11.Device1;
using Stride.Core.Mathematics;
using System.Collections.Concurrent;
using VL.Core.Utils;
using VL.Core;

namespace VL.CEF
{
    public partial class SkiaRenderHandler : ILayer
    {
        private readonly object syncRoot = new object();

        private readonly RenderContext renderContext;
        private SKImage rasterImage, textureImage;
        private readonly Device1 device;
        private readonly Device5 producerDevice;
        private readonly DeviceContext4 producerContext;
        private BlockingCollection<SharpDX.DXGI.Resource> queue = new BlockingCollection<SharpDX.DXGI.Resource>(boundedCapacity: 1);
        private WebBrowser browser;

        public SkiaRenderHandler(WebBrowser browser)
        {
            this.browser = browser;
            renderContext = RenderContext.ForCurrentThread();
            if (renderContext.EglContext.Dislpay.TryGetD3D11Device(out var d3dDevice))
            {
                using var device = new Device(d3dDevice);
                this.device = device.QueryInterface<Device1>();

                using var producerDevice = new Device(SharpDX.Direct3D.DriverType.Hardware);
                this.producerDevice = producerDevice.QueryInterface<Device5>();
                this.producerContext = producerDevice.ImmediateContext.QueryInterface<DeviceContext4>();
            }
            browser.Paint += OnPaint;
            browser.AcceleratedPaint += OnAcceleratedPaint;
        }

        public void Dispose()
        {
            browser.Paint -= OnPaint;
            browser.AcceleratedPaint -= OnAcceleratedPaint;

            rasterImage?.Dispose();
            textureImage?.Dispose();
            queue.CompleteAdding();
            foreach (var t in queue)
                t.Dispose();
            renderContext.Dispose();

            device?.Dispose();
            producerContext?.Dispose();
            producerDevice?.Dispose();
        }

        private void OnPaint(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()), buffer, width * 4);
            lock (syncRoot)
            {
                rasterImage?.Dispose();
                rasterImage = image;
            }
        }

        private void OnAcceleratedPaint(CefPaintElementType type, CefRectangle[] dirtyRects, CefAcceleratedPaintInfo info)
        {
            try
            {
                var sharedHandle = info.SharedTextureHandle;
                if (sharedHandle == IntPtr.Zero)
                    return;

                using var texture = producerDevice.OpenSharedResource1<Texture2D>(sharedHandle);
                if (texture is null)
                    return;

                using var targetTexture = new Texture2D(producerDevice, new()
                {
                    ArraySize = texture.Description.ArraySize,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    Format = Format.B8G8R8A8_UNorm,
                    Height = texture.Description.Height,
                    Width = texture.Description.Width,
                    MipLevels = texture.Description.MipLevels,
                    OptionFlags = ResourceOptionFlags.Shared,
                    SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default
                });

                producerContext.CopyResource(texture, targetTexture);
                producerContext.Flush();

                var resource = targetTexture.QueryInterface<SharpDX.DXGI.Resource>();
                if (!queue.TryAddSafe(resource, millisecondsTimeout: 50))
                    resource.Dispose();
            }
            catch (Exception e)
            {
                RuntimeGraph.ReportException(e);
            }
        }

        public void Render(CallerInfo caller)
        {
            var canvas = caller.Canvas;
            var localClipBounds = canvas.LocalClipBounds;
            var deviceClipBounds = canvas.DeviceClipBounds;

            // Ensure we render in the proper size
            browser.Size = new Vector2(deviceClipBounds.Width, deviceClipBounds.Height);

            lock (syncRoot)
            {
                // Transfer ownership
                if (queue.TryTake(out var resource))
                {
                    textureImage?.Dispose();

                    using (resource)
                    {
                        using var texture = device.OpenSharedResource<Texture2D>(resource.SharedHandle);
                        textureImage = ToImage(texture);
                    }
                }

                var image = textureImage ?? rasterImage;
                if (image != null)
                    canvas.DrawImage(image, localClipBounds);
            }
        }

        public RectangleF? Bounds => null;

        SKImage ToImage(Texture2D texture)
        {    
            // Taken form VL.Video.MediaFoundation
            const int GL_TEXTURE_BINDING_2D = 0x8069;

            var eglContext = renderContext.EglContext;
            using var eglImage = eglContext.CreateImageFromD3D11Texture(texture.NativePointer);

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

            using var backendTexture = new GRBackendTexture(
                width: description.Width,
                height: description.Height,
                mipmapped: false,
                glInfo: glInfo);

            var image = SKImage.FromTexture(
                renderContext.SkiaContext,
                backendTexture,
                GRSurfaceOrigin.TopLeft,
                colorType,
                SKAlphaType.Premul,
                colorspace: SKColorSpace.CreateSrgb(),
                releaseProc: _ =>
                {
                    NativeGles.glDeleteTextures(1, ref textureId);
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
