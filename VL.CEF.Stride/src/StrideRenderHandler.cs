using SharpDX.Direct3D11;
using Stride.Engine;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using System;
using System.Reactive.Disposables;
using VL.Core;
using VL.Lib.Basics.Resources;
using VL.Stride;
using VL.Stride.Input;
using Xilium.CefGlue;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using System.Collections.Concurrent;
using VL.Core.Utils;
using SharpDX;
using DataBox = Stride.Graphics.DataBox;
using ServiceRegistry = VL.Core.ServiceRegistry;

namespace VL.CEF
{
    public sealed partial class StrideRenderHandler : RendererBase
    {
        private readonly object syncRoot = new object();
        private readonly IResourceHandle<Game> gameHandle;
        private readonly WebBrowser browser;
        private readonly Device5 producerDevice;
        private readonly DeviceContext4 producerContext;
        private readonly BlockingCollection<SharpDX.DXGI.Resource> queue = new(boundedCapacity: 1);
        private readonly bool needsRefCountFix;
        private Texture surface;

        public StrideRenderHandler(WebBrowser browser)
        {
            this.browser = browser;

            var strideVersion = typeof(RenderContext).Assembly.GetName().Version;
            needsRefCountFix = strideVersion > new Version(4, 2, 0, 1) && strideVersion < new Version(4, 2, 0, 2293);

            using var producerDevice = new Device(SharpDX.Direct3D.DriverType.Hardware);
            this.producerDevice = producerDevice.QueryInterface<Device5>();
            this.producerContext = producerDevice.ImmediateContext.QueryInterface<DeviceContext4>();

            gameHandle = ServiceRegistry.Current.GetGameHandle();

            var renderContext = RenderContext.GetShared(gameHandle.Resource.Services);
            Initialize(renderContext);

            browser.Paint += OnPaint;
            browser.AcceleratedPaint += OnAcceleratedPaint;
        }

        protected override void Destroy()
        {
            browser.Paint -= OnPaint;
            browser.AcceleratedPaint -= OnAcceleratedPaint;

            queue.CompleteAdding();
            foreach (var item in queue)
                item.Dispose();

            producerContext.Dispose();
            producerDevice.Dispose();

            surface?.Dispose();
            base.Destroy();
            gameHandle.Dispose();
        }

        void OnPaint(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            var data = new DataBox(buffer, width * 4, width * height * 4);
            var format = PixelFormat.B8G8R8A8_UNorm;
            if (GraphicsDevice.ColorSpace == ColorSpace.Linear)
                format = PixelFormat.B8G8R8A8_UNorm_SRgb;

            var surface = Texture.New2D(GraphicsDevice, width, height, 1, format, new[] { data }, usage: GraphicsResourceUsage.Immutable);
            lock (syncRoot)
            {
                this.surface?.Dispose();
                this.surface = surface;
            }
        }

        void OnAcceleratedPaint(CefPaintElementType type, CefRectangle[] dirtyRects, CefAcceleratedPaintInfo info)
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
                    Format = GraphicsDevice.ColorSpace == ColorSpace.Linear ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm,
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

        protected override void DrawCore(RenderDrawContext context)
        {
            // Subscribe to input events - in case we have many sinks we assume that there's only one input source active
            var inputSource = context.RenderContext.Tags.Get(InputExtensions.WindowInputSource);
            if (inputSource != lastInputSource)
            {
                lastInputSource = inputSource;
                inputSubscription.Disposable = SubscribeToInputSource(inputSource, context);
            }

            var commandList = context.CommandList;

            // Ensure we render in the proper size
            browser.Size = commandList.Viewport.Size;

            if (queue.TryTake(out var resource))
            {
                surface?.Dispose();

                var device = (Device)SharpDXInterop.GetNativeDevice(GraphicsDevice);
                using (resource)
                {
                    var texture2D = device.OpenSharedResource<Texture2D>(resource.SharedHandle);

                    surface = SharpDXInterop.CreateTextureFromNative(GraphicsDevice, texture2D, takeOwnership: false);

                    if (needsRefCountFix)
                    {
                        // Undo the ref count increment of an internal QueryInterface call
                        (texture2D as IUnknown).Release();
                    }
                }
            }

            lock (syncRoot)
            {
                var renderTarget = commandList.RenderTarget;
                if (surface != null && renderTarget != null)
                {
                    context.GraphicsContext.DrawTexture(surface, BlendStates.AlphaBlend);
                }
            }
        }
        private readonly SerialDisposable inputSubscription = new SerialDisposable();
        private IInputSource lastInputSource;
    }
}
