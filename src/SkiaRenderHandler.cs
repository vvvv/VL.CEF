using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Platform.Windows;
using SharpDX.Direct3D11;
using SkiaSharp;
using Stride.Core.Mathematics;
using System;
using VL.Core;
using VL.Lib.Basics.Resources;
using VL.Skia;
using Xilium.CefGlue;
using Vector2 = Stride.Core.Mathematics.Vector2;
using PixelFormat = SharpDX.DXGI.Format;
using All = OpenTK.Graphics.OpenGL.All;
using System.Collections.Generic;
using System.Linq;

namespace VL.CEF
{
    public sealed partial class SkiaRenderHandler : IRenderHandler, ILayer
    {
        private readonly object syncRoot = new object();
        private readonly IResourceHandle<Device> deviceHandle;
        private IntPtr wglDeviceHandle;
        private IntPtr sharedSurfaceHandle;
        private SKImage surface;
        private WebRenderer webRenderer;

        public SkiaRenderHandler(NodeContext nodeContext)
        {
            var deviceProvider = nodeContext.Factory.CreateService<IResourceProvider<Device>>(nodeContext);
            deviceHandle = deviceProvider?.GetHandle();
        }

        public bool UseAcceleratedPaint => wglDeviceHandle != default;

        public void Initialize(WebRenderer webRenderer)
        {
            this.webRenderer = webRenderer;
        }

        public void Dispose()
        {
            surface?.Dispose();
            foreach (var sharedTexture in sharedTextures.Values)
                sharedTexture?.Dispose();
            if (wglDeviceHandle != default)
                Wgl.DXCloseDeviceNV(wglDeviceHandle);
            deviceHandle?.Dispose();
        }

        public void OnPaint(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            if (UseAcceleratedPaint)
                return;

            var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()), buffer, width * 4);
            lock (syncRoot)
            {
                surface?.Dispose();
                surface = image;
            }
        }

        public void OnAcceleratedPaint(CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle)
        {
            sharedSurfaceHandle = sharedHandle;
        }

        public void Render(CallerInfo caller)
        {
            var canvas = caller.Canvas;
            var localClipBounds = canvas.LocalClipBounds;
            var deviceClipBounds = canvas.DeviceClipBounds;

            // Ensure we render in the proper size
            webRenderer.Size = new Vector2(deviceClipBounds.Width, deviceClipBounds.Height);

            if (!UseAcceleratedPaint)
            {
                lock (syncRoot)
                {
                    if (surface != null)
                    {
                        canvas.DrawImage(surface, localClipBounds);
                    }
                }
            }
            else
            {
                var sharedHandle = sharedSurfaceHandle;
                if (sharedHandle == IntPtr.Zero)
                    return;

                var device = deviceHandle.Resource;
                if (device is null)
                    return;

                if (wglDeviceHandle == IntPtr.Zero)
                {
                    // We assume that an OpenGL context is current
                    wglDeviceHandle = Wgl.DXOpenDeviceNV(device.NativePointer);
                    if (wglDeviceHandle == IntPtr.Zero)
                        return;
                }

                if (!sharedTextures.TryGetValue(sharedHandle, out var sharedTexture))
                {
                    var texture = device.OpenSharedResource<Texture2D>(sharedHandle);
                    foreach (var entry in sharedTextures.ToList())
                    {
                        var description = entry.Value.Texture.Description;
                        if (description.Width != texture.Description.Width || description.Height != texture.Description.Height || description.Format != texture.Description.Format)
                        {
                            sharedTextures.Remove(entry.Key);
                            entry.Value.Dispose();
                        }
                    }
                    sharedTexture = SharedTextureImage.Create(caller.GRContext, wglDeviceHandle, texture);
                    sharedTextures.Add(sharedHandle, sharedTexture);
                }

                if (sharedTexture != null && sharedTexture.Lock())
                {
                    canvas.DrawImage(sharedTexture.Image, localClipBounds);
                    canvas.Flush();
                    sharedTexture.Unlock();
                }
            }
        }
        private readonly Dictionary<IntPtr, SharedTextureImage> sharedTextures = new Dictionary<IntPtr, SharedTextureImage>();

        sealed class SharedTextureImage : IDisposable
        {
            public static SharedTextureImage Create(GRContext context, IntPtr deviceHandle, Texture2D texture)
            {
                var name = (uint)GL.GenTexture();
                var textureHandle = Wgl.DXRegisterObjectNV(
                    deviceHandle,
                    texture.NativePointer,
                    name,
                    (uint)All.Texture2D,
                    WGL_NV_DX_interop.AccessReadOnly);

                if (textureHandle == default)
                {
                    Wgl.DXUnregisterObjectNV(deviceHandle, textureHandle);
                    GL.DeleteTexture(name);
                    return null;
                }

                var pixelFormat = PixelFormat.R8G8B8A8_UNorm;
                var pixelConfig = GetPixelConfig(pixelFormat);
                var colorType = pixelConfig.ToColorType();
                var backendTexture = new GRBackendTexture(
                    texture.Description.Width,
                    texture.Description.Height,
                    mipmapped: false,
                    new GRGlTextureInfo((uint)All.Texture2D, name, pixelConfig.ToGlSizedFormat()));

                var image = SKImage.FromTexture(
                    context,
                    backendTexture,
                    GRSurfaceOrigin.BottomLeft,
                    colorType,
                    SKAlphaType.Premul,
                    colorspace: null /*SKColorSpace.CreateSrgb()*/,
                    releaseProc: _ =>
                    {
                    });

                if (image is null)
                {
                    Wgl.DXUnregisterObjectNV(deviceHandle, textureHandle);
                    GL.DeleteTexture(name);
                    return null;
                }

                return new SharedTextureImage(image, texture, textureHandle, deviceHandle, name);
            }

            static GRPixelConfig GetPixelConfig(PixelFormat format)
            {
                switch (format)
                {
                    case PixelFormat.B8G8R8A8_UNorm:
                        return GRPixelConfig.Bgra8888;
                    case PixelFormat.B8G8R8A8_UNorm_SRgb:
                        return GRPixelConfig.Sbgra8888;
                    case PixelFormat.R8G8B8A8_UNorm:
                        return GRPixelConfig.Rgba8888;
                    case PixelFormat.R8G8B8A8_UNorm_SRgb:
                        return GRPixelConfig.Srgba8888;
                    case PixelFormat.R10G10B10A2_UNorm:
                        return GRPixelConfig.Rgba1010102;
                    case PixelFormat.R16G16B16A16_Float:
                        return GRPixelConfig.RgbaHalf;
                    case PixelFormat.R32G32B32A32_Float:
                        return GRPixelConfig.RgbaFloat;
                    case PixelFormat.R32G32_Float:
                        return GRPixelConfig.RgFloat;
                    case PixelFormat.A8_UNorm:
                        return GRPixelConfig.Alpha8;
                    case PixelFormat.R8_UNorm:
                        return GRPixelConfig.Gray8;
                    case PixelFormat.B5G6R5_UNorm:
                        return GRPixelConfig.Rgb565;
                    default:
                        return GRPixelConfig.Unknown;
                }
            }

            public readonly SKImage Image;
            public readonly Texture2D Texture;
            public readonly IntPtr TextureHandle;
            public readonly IntPtr DeviceHandle;
            public readonly uint Name;

            public SharedTextureImage(SKImage image, Texture2D texture, IntPtr textureHandle, IntPtr deviceHandle, uint name)
            {
                Image = image;
                Texture = texture;
                TextureHandle = textureHandle;
                DeviceHandle = deviceHandle;
                Name = name;
            }

            public bool Lock()
            {
                unsafe
                {
                    void** objects = stackalloc void*[1];

                    objects[0] = TextureHandle.ToPointer();

                    return Wgl.DXLockObjectsNV(DeviceHandle, 1, new IntPtr(objects));
                }
            }

            public bool Unlock()
            {
                unsafe
                {
                    void** objects = stackalloc void*[1];

                    objects[0] = TextureHandle.ToPointer();

                    return Wgl.DXUnlockObjectsNV(DeviceHandle, 1, new IntPtr(objects));
                }
            }

            public void Dispose()
            {
                Image.Dispose();
                Wgl.DXUnregisterObjectNV(DeviceHandle, TextureHandle);
                GL.DeleteTexture(Name);
                Texture.Dispose();
            }
        }
    }
}
