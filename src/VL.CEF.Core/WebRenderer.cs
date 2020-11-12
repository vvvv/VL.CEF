using System;
using Xilium.CefGlue;
using System.Globalization;
using System.Xml.Linq;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Core;
using MapMode = VL.Core.MapMode;

namespace VL.CEF
{
    public interface IRenderHandler : IDisposable
    {
        bool UseAcceleratedPaint { get; }

        void Initialize(WebRenderer renderer);

        void OnPaint(CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height);

        void OnAcceleratedPaint(CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle);
    }

    public abstract class WebRenderer
    {
        public abstract CefBrowserHost BrowserHost { get; }
        public abstract Vector2 Size { get; set; }
        public abstract float ScaleFactor { get; set; }
    }

    public sealed class WebRenderer<TRenderHandler> : WebRenderer, IDisposable
        where TRenderHandler : IRenderHandler
    {
        private volatile bool FEnabled = true;
        private readonly IDisposable FRuntimeHandle;
        private readonly WebClient<TRenderHandler> FWebClient;
        private CefBrowser FBrowser;
        private CefRequestContext FRequestContext;
        private CefBrowserHost FBrowserHost;
        private string FUrl;
        private string FContent;
        private Vector2 FSize;
        private readonly AutoResetEvent FBrowserAttachedEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent FBrowserDetachedEvent = new AutoResetEvent(false);
        private readonly TRenderHandler FRenderHandler;

        public WebRenderer(TRenderHandler renderHandler)
        {
            FRenderHandler = renderHandler;
            FRuntimeHandle = CefExtensions.GetRuntimeProvider().GetHandle();

            var settings = new CefBrowserSettings();
            settings.FileAccessFromFileUrls = CefState.Enabled;
            settings.Plugins = CefState.Enabled;
            settings.RemoteFonts = CefState.Enabled;
            settings.UniversalAccessFromFileUrls = CefState.Enabled;
            settings.WebGL = CefState.Enabled;
            settings.WebSecurity = CefState.Disabled;

            var windowInfo = CefWindowInfo.Create();
            windowInfo.ExternalBeginFrameEnabled = true;
            windowInfo.SharedTextureEnabled = renderHandler.UseAcceleratedPaint;
            windowInfo.SetAsWindowless(IntPtr.Zero, true);

            FWebClient = new WebClient<TRenderHandler>(this);
            // See http://magpcss.org/ceforum/viewtopic.php?f=6&t=5901
            // We need to maintain different request contexts in order to have different zoom levels
            // See https://bitbucket.org/chromiumembedded/cef/issues/1314
            var rcSettings = new CefRequestContextSettings()
            {
                IgnoreCertificateErrors = true
            };
            FRequestContext = CefRequestContext.CreateContext(rcSettings, new WebClient<TRenderHandler>.RequestContextHandler());
            CefBrowserHost.CreateBrowser(windowInfo, FWebClient, settings, "about:blank", requestContext: FRequestContext);
            // Block until browser is created
            FBrowserAttachedEvent.WaitOne();

            FRenderHandler.Initialize(this);
        }

        internal void Attach(CefBrowser browser)
        {
            FBrowser = browser;
            FBrowserHost = browser.GetHost();
            FBrowserHost.SetMouseCursorChangeDisabled(true);
            FBrowserAttachedEvent.Set();
        }

        internal void Detach()
        {
            FBrowser.Dispose();
            FBrowserDetachedEvent.Set();
        }

        public void Dispose()
        {
            FBrowserHost.CloseBrowser(true);
            FBrowserDetachedEvent.WaitOne();
            FBrowserAttachedEvent.Dispose();
            FBrowserDetachedEvent.Dispose();
            FRequestContext.Dispose();
            FRenderHandler.Dispose();
            FRuntimeHandle.Dispose();
        }

        public void LoadUrl(string url)
        {
            // Normalize inputs
            url = string.IsNullOrEmpty(url) ? "about:blank" : url;
            if (FUrl != url)
            {
                // Set new values
                FUrl = url;
                FContent = null;
                // Reset all computed values
                Reset();
                using (var mainFrame = FBrowser.GetMainFrame())
                {
                    // Create a valid url
                    var uri = new UriBuilder(FUrl).Uri;
                    mainFrame.LoadUrl(uri.ToString());
                }
            }
        }

        public void LoadString(string content, string baseUrl)
        {
            // Normalize inputs
            if (FUrl != baseUrl || FContent != content)
            {
                // Set new values
                FUrl = baseUrl;
                FContent = content;
                // Reset all computed values
                Reset();
                using (var mainFrame = FBrowser.GetMainFrame())
                {
                    // Create a valid url
                    var uri = new UriBuilder(baseUrl).Uri;
                    if (uri.IsFile && !uri.ToString().EndsWith("/"))
                        // Append trailing slash
                        uri = new UriBuilder(uri.ToString() + "/").Uri;
                    mainFrame.LoadString(content ?? string.Empty, baseUrl);
                }
            }
        }

        public TRenderHandler Update()
        {
            FBrowserHost.SendExternalBeginFrame();
            return FRenderHandler;
        }

        private void Reset()
        {
            Loaded = false;
            DocumentSize = default;
            ErrorText = default;
        }

        public void ExecuteJavaScript(string javaScript)
        {
            using (var mainFrame = FBrowser.GetMainFrame())
            {
                mainFrame.ExecuteJavaScript(javaScript, string.Empty, 0);
            }
        }

        internal void UpdateDocumentSize()
        {
            using (var frame = FBrowser.GetMainFrame())
            {
                var request = CefProcessMessage.Create("document-size-request");
                frame.SendProcessMessage(CefProcessId.Renderer, request);
            }
        }

        internal void OnDocumentSize(CefFrame frame, int width, int height)
        {
            if (frame.IsMain)
            {
                // Retrieve the current size
                var size = Size;
                // Set the new values
                DocumentSize = new Vector2(width, height);
            }
        }

        public void Reload()
        {
            Loaded = false;
            FBrowser.Reload();
        }

        public override CefBrowserHost BrowserHost => FBrowserHost;

        public override Vector2 Size
        {
            get => FSize;
            set
            {
                if (value != FSize)
                {
                    FSize = value;
                    FBrowserHost.WasResized();
                }
            }
        }

        public override float ScaleFactor
        {
            get => scaleFactor;
            set
            {
                if (value != scaleFactor)
                {
                    scaleFactor = value;
                    FBrowserHost.NotifyScreenInfoChanged();
                    FBrowserHost.WasResized();
                }
            }
        }
        float scaleFactor = 1f;

        public Vector2? DocumentSize { get; private set; }

        public float ZoomLevel
        {
            get { return FZoomLevel; }
            set
            {
                if (FZoomLevel != value)
                {
                    FZoomLevel = value;
                    FBrowserHost.SetZoomLevel(value);
                }
            }
        }
        float FZoomLevel;

        public Vector2 ScrollTo
        {
            get { return FScrollTo; }
            set
            {
                if (FScrollTo != value)
                {
                    using (var mainFrame = FBrowser.GetMainFrame())
                    {
                        var x = VLMath.Map(value.X, 0, 1, 0, 1, MapMode.Clamp);
                        var y = VLMath.Map(value.Y, 0, 1, 0, 1, MapMode.Clamp);
                        mainFrame.ExecuteJavaScript(
                            string.Format(CultureInfo.InvariantCulture,
                                @"
                            var body = document.body,
                                html = document.documentElement;
                            var width = Math.max(body.scrollWidth, body.offsetWidth, html.clientWidth, html.scrollWidth, html.offsetWidth);
                            var height = Math.max(body.scrollHeight, body.offsetHeight, html.clientHeight, html.scrollHeight, html.offsetHeight);
                            window.scrollTo({0} *  width, {1} * height);
                            ",
                                 x,
                                 y
                            ),
                            string.Empty,
                            0);
                    }
                    FScrollTo = value;
                }
            }
        }
        Vector2 FScrollTo;

        public bool IsLoading { get; private set; }

        public bool Loaded { get; private set; }

        public string ErrorText { get; private set; }

        public bool Enabled
        {
            get { return FEnabled; }
            set
            {
                if (FEnabled != value)
                {
                    if (FEnabled)
                    {
                        FBrowser.StopLoad();
                        FBrowserHost.WasHidden(true);
                    }
                    FEnabled = value;
                    if (FEnabled)
                    {
                        FBrowserHost.WasHidden(false);
                        FBrowserHost.Invalidate(CefPaintElementType.View);
                    }
                }
            }
        }

        internal void OnLoadError(CefFrame frame, CefErrorCode errorCode, string failedUrl, string errorText)
        {
            if (frame.IsMain)
            {
                ErrorText = errorText;
            }
        }

        internal void OnLoadingStateChange(bool isLoading, bool canGoBack, bool canGoForward)
        {
            IsLoading = isLoading;
            if (!isLoading)
            {
                UpdateDocumentSize();
                Loaded = true;
                // HACK: Re-apply zooming level :/ - https://vvvv.org/forum/htmltexture-bug-with-zoomlevel
                FBrowserHost.SetZoomLevel(ZoomLevel);
            }
            else
                // Reset computed values like document size or DOM
                Reset();
        }

        internal void OnPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] cefRects, IntPtr buffer, int width, int height)
        {
            // Do nothing if disabled
            if (!FEnabled) 
                return;

            if (type == CefPaintElementType.View)
            {
                FRenderHandler.OnPaint(type, cefRects, buffer, width, height);
            }
        }

        internal void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle)
        {
            // Do nothing if disabled
            if (!FEnabled)
                return;

            if (type == CefPaintElementType.View)
            {
                FRenderHandler.OnAcceleratedPaint(type, dirtyRects, sharedHandle);
            }
        }
    }
}
