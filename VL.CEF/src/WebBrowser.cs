using System;
using Xilium.CefGlue;
using System.Globalization;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Core;
using MapMode = VL.Core.MapMode;
using VL.Lib.Collections;
using System.Reactive.Subjects;
using System.Text;

namespace VL.CEF
{
    public sealed partial class WebBrowser : CefClient, IDisposable
    {
        private volatile bool FEnabled = true;
        private readonly IDisposable FRuntimeHandle;
        private CefBrowser FBrowser;
        private CefRequestContext FRequestContext;
        private CefBrowserHost FBrowserHost;
        private Vector2 ComittedSize;
        private Spread<QueryHandler> FQueryHandlers;
        private readonly AutoResetEvent FBrowserAttachedEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent FBrowserDetachedEvent = new AutoResetEvent(false);
        private readonly Subject<CefTextInputMode> FVirtualKeyboardRequests = new Subject<CefTextInputMode>();

        private readonly CefRenderHandler renderHandler;
        private readonly CefLifeSpanHandler lifeSpanHandler;
        private readonly CefLoadHandler loadHandler;
        private readonly CefKeyboardHandler keyboardHandler;
        private readonly CefDisplayHandler displayHandler;
        private readonly CefContextMenuHandler contextMenuHandler;

        public WebBrowser(string url = "about:blank", bool sharedTextureEnabled = true)
        {
            FRuntimeHandle = CefExtensions.GetRuntimeProvider().GetHandle();

            var settings = new CefBrowserSettings();
            settings.RemoteFonts = CefState.Enabled;
            settings.WebGL = CefState.Enabled;
            // Some parameters are gone here compared to old version:
            // - FileAccessFromFileUrls & UniversalAccessFromFileUrls seem related to LoadString baseUrl
            // this is probably now accessible through the new api: ResourceManager which is not available
            // through CefGlue
            // - Plugins: probably also accesible through the new CefExtensionsHandler API
            // - WebSecurity was disabled but that's not available anymore

            var windowInfo = CefWindowInfo.Create();
            windowInfo.ExternalBeginFrameEnabled = true;
            windowInfo.SharedTextureEnabled = sharedTextureEnabled;
            windowInfo.SetAsWindowless(IntPtr.Zero, true);

            renderHandler = new RenderHandler(this);
            lifeSpanHandler = new LifeSpanHandler(this);
            loadHandler = new LoadHandler(this);
            keyboardHandler = new KeyboardHandler();
            displayHandler = new DisplayHandler(this);
            contextMenuHandler = new ContextMenuHandler();

            var rcSettings = new CefRequestContextSettings();
            FRequestContext = CefRequestContext.CreateContext(rcSettings, new RequestContextHandler());
            CefBrowserHost.CreateBrowser(windowInfo, this, settings, string.IsNullOrEmpty(url) ? "about:blank" : url, requestContext: FRequestContext);
            // Block until browser is created
            FBrowserAttachedEvent.WaitOne();
        }

        internal event PaintHandler Paint;

        internal event AcceleratedPaintHandler AcceleratedPaint;

        internal CefBrowserHost BrowserHost => FBrowserHost;

        internal void Attach(CefBrowser browser)
        {
            FBrowser = browser;
            FBrowserHost = browser.GetHost();
            FBrowserAttachedEvent.Set();
        }

        internal void Detach()
        {
            FBrowser.Dispose();
            FBrowserDetachedEvent.Set();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            FBrowserHost.CloseBrowser(true);
            FBrowserDetachedEvent.WaitOne();
            FBrowserAttachedEvent.Dispose();
            FBrowserDetachedEvent.Dispose();
            FRequestContext.Dispose();
            FRuntimeHandle.Dispose();
        }

        /// <summary>
        /// Loads the given URL.
        /// </summary>
        public void LoadUrl(string url)
        {
            // Reset all computed values
            Reset();

            FBrowser.StopLoad();

            using (var mainFrame = FBrowser.GetMainFrame())
            {
                // Create a valid url
                var uri = new UriBuilder(string.IsNullOrEmpty(url) ? "about:blank" : url).Uri;
                mainFrame.LoadUrl(uri.ToString());
            }
        }

        /// <summary>
        /// Loads the given content using the base URL to resolve relative paths.
        /// </summary>
        public void LoadString(string content)
        {
            // Reset all computed values
            Reset();

            FBrowser.StopLoad();
            // TODO: CEF used to have a LoadString function accepting a baseUrl
            // which is now gone so we replace it with a data url with the content
            // encoded as base64 but the baseUrl can't be simulated this way.
            // From c/c++ there's a way to do this using a CefResourceManager 
            // but it's not exported by CefGlue so it would need custom bindings.
            // More info at: https://magpcss.org/ceforum/viewtopic.php?f=6&t=17231
            using (var mainFrame = FBrowser.GetMainFrame())
            {
                var mime_type = "text/html";
                var bytes = Encoding.UTF8.GetBytes(content);
                string dataUrl = "data:" + mime_type + ";base64," +
                    Uri.EscapeDataString(Convert.ToBase64String(bytes));
                mainFrame.LoadUrl(dataUrl);
            }
        }

        /// <summary>
        /// Issue a BeginFrame request to Chromium.
        /// </summary>
        public void Update()
        {
            if (ComittedSize != Size)
            {
                ComittedSize = Size;
                FBrowserHost.WasResized();
            }
            FBrowserHost.SendExternalBeginFrame();
        }

        /// <summary>
        /// Runs the given script.
        /// </summary>
        public void ExecuteJavaScript(string javaScript)
        {
            using (var mainFrame = FBrowser.GetMainFrame())
            {
                mainFrame.ExecuteJavaScript(javaScript, string.Empty, 0);
            }
        }

        /// <summary>
        /// Returns true if the browser can navigate backwards.
        /// </summary>
        public bool CanGoBack => FBrowser.CanGoBack;

        /// <summary>
        /// Returns true if the browser can navigate forwards.
        /// </summary>
        public bool CanGoForward => FBrowser.CanGoForward;

        /// <summary>
        /// Navigate backwards.
        /// </summary>
        public void GoBack() => FBrowser.GoBack();

        /// <summary>
        /// Navigate forwards.
        /// </summary>
        public void GoForward() => FBrowser.GoForward();

        /// <summary>
        /// Reload the current page.
        /// </summary>
        public void Reload()
        {
            Loaded = false;
            FBrowser.Reload();
        }

        /// <summary>
        /// Scrolls to the given position.
        /// </summary>
        public void ScrollTo(Vector2 value)
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
        }

        /// <summary>
        /// Set whether the browser's audio is muted.
        /// </summary>
        public void SetAudioMuted(bool value) => BrowserHost.SetAudioMuted(value);

        /// <summary>
        /// The size of the browser in pixel.
        /// </summary>
        public Vector2 Size { get; set; }
        /// <summary>
        /// The scale factor of the screen.
        /// </summary>
        public float ScaleFactor
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

        /// <summary>
        /// The size of the loaded document.
        /// </summary>
        public Vector2? DocumentSize { get; private set; }

        /// <summary>
        /// The content zoom level.
        /// </summary>
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

        /// <summary>
        /// The currently loaded URL.
        /// </summary>
        public string Url => FBrowser.GetMainFrame().Url;

        /// <summary>
        /// Raised when an on-screen keyboard should be shown or hidden. 
        /// The input mode specifies what kind of keyboard should be opened.
        /// If input mode is CefTextInputMode.None, any existing keyboard for this browser should be hidden.
        /// </summary>
        public IObservable<CefTextInputMode> VirtualKeyboardRequests => FVirtualKeyboardRequests;

        /// <summary>
        /// Whether the browser is currently loading content.
        /// </summary>
        public bool IsLoading { get; private set; }

        /// <summary>
        /// Whether the content is loaded.
        /// </summary>
        public bool Loaded { get; private set; }

        /// <summary>
        /// The error (if any) should loading of the content have failed.
        /// </summary>
        public string ErrorText { get; private set; }

        /// <summary>
        /// The query handlers which get invoked by the JavaScript engine.
        /// </summary>
        public Spread<QueryHandler> QueryHandlers 
        {
            private get => FQueryHandlers;
            set
            {
                if (value != FQueryHandlers)
                {
                    FQueryHandlers = value;
                    // Doesn't work, renderer stays blank on open. Probably something off when loading from string.
                    //Reload();
                }
            }
        }

        /// <summary>
        /// Whether or not the browser is enabled.
        /// </summary>
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

        private void Reset()
        {
            Loaded = false;
            DocumentSize = default;
            ErrorText = default;
        }

        private void UpdateDocumentSize()
        {
            using (var frame = FBrowser.GetMainFrame())
            {
                var request = CefProcessMessage.Create("document-size-request");
                frame.SendProcessMessage(CefProcessId.Renderer, request);
            }
        }
    }
}
