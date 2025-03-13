using SharpDX.Direct3D11;
using Stride.Core.IO;
using Stride.Engine;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Images;
using Stride.Shaders.Compiler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Disposables;
using System.Text;
using VL.Core;
using VL.Lib.Basics.Resources;
using VL.Stride;
using VL.Stride.Input;
using Xilium.CefGlue;
using System.Reflection;
using SharpDX.DXGI;
using Device1 = SharpDX.Direct3D11.Device1;
using Device = SharpDX.Direct3D11.Device;
using System.Collections.Concurrent;
using VL.Core.Utils;
using SharpDX;
using DataBox = Stride.Graphics.DataBox;
using System.Threading;
using System.Diagnostics;
using Stride.Core;
using ServiceRegistry = VL.Core.ServiceRegistry;

namespace VL.CEF
{
    public sealed partial class StrideRenderHandler : RendererBase
    {
        struct Frame(SharpDX.DXGI.Resource resource, long fenceValue)
        {
            public SharpDX.DXGI.Resource Resource = resource;
            public long FenceValue = fenceValue;
        }

        private readonly object syncRoot = new object();
        private readonly IResourceHandle<Game> gameHandle;
        private readonly WebBrowser browser;
        private readonly Device5 producerDevice;
        private readonly DeviceContext4 producerContext;
        private readonly Fence copyFence;
        private readonly nint copyFenceHandle;
        private long copyFenceValue;
        private readonly AutoResetEvent copyEvent = new(false);
        private readonly BlockingCollection<Frame> queue = new(boundedCapacity: 1);
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
            this.copyFence = new Fence(this.producerDevice, copyFenceValue, FenceFlags.Shared);
            this.copyFenceHandle = copyFence.CreateSharedHandle(default, 0x10000000, null);

            gameHandle = ServiceRegistry.Current.GetGameHandle();

            var renderContext = RenderContext.GetShared(gameHandle.Resource.Services);
            Initialize(renderContext);

            {
                var strideDevice = SharpDXInterop.GetNativeDevice(renderContext.GraphicsDevice) as Device;
                using var strideDevice5 = strideDevice.QueryInterface<Device5>();
                this.copyFenceStride = strideDevice5.OpenSharedFence(copyFenceHandle);
            }

            browser.Paint += OnPaint;
            browser.AcceleratedPaint += OnAcceleratedPaint;
        }

        protected override void Destroy()
        {
            browser.Paint -= OnPaint;
            browser.AcceleratedPaint -= OnAcceleratedPaint;

            queue.CompleteAdding();
            foreach (var item in queue)
                item.Resource.Dispose();

            copyEvent.Set();
            copyEvent.Dispose();
            copyFence.Dispose();
            copyFenceStride.Dispose();
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

                // Tell GPU to copy the texture
                producerContext.CopyResource(texture, targetTexture);
                // Prepare next fence
                var nextFenceValue = ++copyFenceValue;
                // Once GPU reaches next fence, set wait event to signaled (unblocks waiting thread)
                copyFence.SetEventOnCompletion(nextFenceValue, copyEvent.Handle);
                // Inject next fence into command list of GPU
                producerContext.Signal(copyFence, nextFenceValue);
                // Ensure commands (COPY, SIGNAL) get uploaded to GPU
                producerContext.Flush();

                var resource = targetTexture.QueryInterface<SharpDX.DXGI.Resource>();
                if (queue.TryAddSafe(new Frame(resource, nextFenceValue), millisecondsTimeout: 100))
                {
                    // Render thread just opened the target texture. It uses the same fence to tell the GPU about the copy operation.
                    // But here on our end we'll need to block until texture copy on GPU is truly finished, or CEF would start rendering
                    // into our texture again.
                    copyEvent.WaitOne();
                }
                else
                {
                    resource.Dispose();
                }
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

            if (queue.TryTake(out var frame))
            {
                surface?.Dispose();

                var device = (Device)SharpDXInterop.GetNativeDevice(GraphicsDevice);
                var resource = frame.Resource;
                var texture2D = device.OpenSharedResource<Texture2D>(resource.SharedHandle);
                resource.Dispose();

                surface = SharpDXInterop.CreateTextureFromNative(GraphicsDevice, texture2D, takeOwnership: false);

                if (needsRefCountFix)
                {
                    // Undo the ref count increment of an internal QueryInterface call
                    (texture2D as IUnknown).Release();
                }

                // Ensure GPU waits on copy operation to finish
                using var deviceContext = ((DeviceContext)SharpDXInterop.GetNativeDeviceContext(GraphicsDevice)).QueryInterface<DeviceContext4>();
                deviceContext.Wait(copyFenceStride, frame.FenceValue);
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
        private readonly Fence copyFenceStride;
        private IInputSource lastInputSource;
    }
}
