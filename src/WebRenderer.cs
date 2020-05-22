using System;
using Xilium.CefGlue;
using System.Globalization;
using System.Xml.Linq;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;
using System.Reactive.Subjects;
using VL.Lib.Basics.Imaging;
using Xenko.Core.Mathematics;
using VL.Core;

namespace VL.CEF
{
    public class WebRenderer : IDisposable
    {
        public const string DEFAULT_URL = "http://vvvv.org";
        public const string DEFAULT_CONTENT = @"<html><head></head><body bgcolor=""#ffffff""></body></html>";
        public const int DEFAULT_WIDTH = 800;
        public const int DEFAULT_HEIGHT = 600;
        public const int MIN_FRAME_RATE = 1;
        public const int MAX_FRAME_RATE = 60;

        private volatile bool FEnabled = true;
        private readonly IDisposable FRuntimeHandle;
        private readonly WebClient FWebClient;
        private readonly Subject<IImage> FImages = new Subject<IImage>();
        private CefBrowser FBrowser;
        private CefRequestContext FRequestContext;
        private CefBrowserHost FBrowserHost;
        private string FUrl;
        private string FHtml;
        private bool FDomIsValid;
        private XDocument FCurrentDom;
        private string FCurrentUrl;
        private string FErrorText;
        private Vector2 FSize;
        private XElement FReceivedData;
        private readonly AutoResetEvent FBrowserAttachedEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent FBrowserDetachedEvent = new AutoResetEvent(false);

        /// <summary>
        /// Create a new texture renderer.
        /// </summary>
        /// <param name="logger">The logger to log to.</param>
        /// <param name="frameRate">
        /// The maximum rate in frames per second (fps) that CefRenderHandler::OnPaint will
        /// be called for a windowless browser. The actual fps may be lower if the browser
        /// cannot generate frames at the requested rate. The minimum value is 1 and the 
        /// maximum value is 60 (default 30).
        /// </param>
        public WebRenderer(int frameRate = 30)
        {
            FRuntimeHandle = CefExtensions.GetRuntimeProvider().GetHandle();

            FrameRate = VLMath.Clamp(frameRate, MIN_FRAME_RATE, MAX_FRAME_RATE);

            FLoaded = false;

            var settings = new CefBrowserSettings();
            settings.FileAccessFromFileUrls = CefState.Enabled;
            settings.Plugins = CefState.Enabled;
            settings.RemoteFonts = CefState.Enabled;
            settings.UniversalAccessFromFileUrls = CefState.Enabled;
            settings.WebGL = CefState.Enabled;
            settings.WebSecurity = CefState.Disabled;
            settings.WindowlessFrameRate = VLMath.Clamp(frameRate, MIN_FRAME_RATE, MAX_FRAME_RATE);

            var windowInfo = CefWindowInfo.Create();
            windowInfo.SetAsWindowless(IntPtr.Zero, true);

            FWebClient = new WebClient(this);
            // See http://magpcss.org/ceforum/viewtopic.php?f=6&t=5901
            // We need to maintain different request contexts in order to have different zoom levels
            // See https://bitbucket.org/chromiumembedded/cef/issues/1314
            var rcSettings = new CefRequestContextSettings()
            {
                IgnoreCertificateErrors = true
            };
            FRequestContext = CefRequestContext.CreateContext(rcSettings, new WebClient.RequestContextHandler());
            CefBrowserHost.CreateBrowser(windowInfo, FWebClient, settings, FRequestContext);
            // Block until browser is created
            FBrowserAttachedEvent.WaitOne();
        }

        public int FrameRate { get; private set; }

        public IObservable<IImage> Images => FImages;

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
            FMouseSubscription?.Dispose();
            FKeyboardSubscription?.Dispose();
            FRuntimeHandle.Dispose();
        }

        public void LoadUrl(string url)
        {
            LoadUrl(FSize, url);
        }

        public void LoadUrl(Vector2 size, string url)
        {
            // Normalize inputs
            url = string.IsNullOrEmpty(url) ? "about:blank" : url;
            if (FUrl != url || FSize != size)
            {
                // Set new values
                FUrl = url;
                FHtml = null;
                Size = size;
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

        public void LoadString(Vector2 size, string html, string baseUrl, string patchPath)
        {
            // Normalize inputs
            baseUrl = string.IsNullOrEmpty(baseUrl) ? patchPath : baseUrl;
            html = html ?? string.Empty;
            if (FUrl != baseUrl || FHtml != html || FSize != size)
            {
                // Set new values
                FUrl = baseUrl;
                FHtml = html;
                Size = size;
                // Reset all computed values
                Reset();
                using (var mainFrame = FBrowser.GetMainFrame())
                {
                    // Create a valid url
                    var uri = new UriBuilder(baseUrl).Uri;
                    if (uri.IsFile && !uri.ToString().EndsWith("/"))
                        // Append trailing slash
                        uri = new UriBuilder(uri.ToString() + "/").Uri;
                    mainFrame.LoadUrl(baseUrl);
                }
            }
        }

        private void Reset()
        {
            FLoaded = false;
            FDocumentSizeIsValid = false;
            FDomIsValid = false;
            FErrorText = string.Empty;
            if (IsAutoSize)
                FBrowserHost.WasResized();
        }

        public void ExecuteJavaScript(string javaScript)
        {
            using (var mainFrame = FBrowser.GetMainFrame())
            {
                mainFrame.ExecuteJavaScript(javaScript, string.Empty, 0);
            }
        }

        public void UpdateDom()
        {
            using (var frame = FBrowser.GetMainFrame())
                UpdateDom(frame);
        }

        private void UpdateDom(CefFrame frame)
        {
            var request = CefProcessMessage.Create("dom-request");
            request.SetFrameIdentifier(frame.Identifier);
            FBrowser.SendProcessMessage(CefProcessId.Renderer, request);
        }

        internal void OnUpdateDom(XDocument dom)
        {
            FCurrentDom = dom;
            FDomIsValid = true;
            FErrorText = null;
        }

        internal void OnUpdateDom(string error)
        {
            FCurrentDom = null;
            FDomIsValid = true;
            FErrorText = error;
        }

        public void UpdateDocumentSize()
        {
            using (var frame = FBrowser.GetMainFrame())
            {
                var request = CefProcessMessage.Create("document-size-request");
                request.SetFrameIdentifier(frame.Identifier);
                FBrowser.SendProcessMessage(CefProcessId.Renderer, request);
            }
        }

        internal void OnDocumentSize(CefFrame frame, int width, int height)
        {
            if (frame.IsMain)
            {
                // Retrieve the current size
                var size = Size;
                // Set the new values
                FDocumentSize = new Vector2(width, height);
                FDocumentSizeIsValid = true;
                // Notify the browser about the change in case the size was affected
                // by this change
                var newSize = Size;
                if (IsAutoSize && size != newSize)
                {
                    FBrowserHost.WasResized();
                    FBrowserHost.Invalidate(CefPaintElementType.View);
                }
            }
        }

        internal void OnReceiveData(CefFrame frame, CefDictionaryValue data)
        {
            FReceivedData = ToXElement("data", data);
        }

        internal void OnReceiveData(CefFrame frame, CefListValue data)
        {
            FReceivedData = ToXElement("data", data);
        }

        static XElement ToXElement(string name, CefDictionaryValue value)
        {
            var result = new XElement(name);
            var keys = value.GetKeys();
            foreach (var key in keys)
            {
                var type = value.GetValueType(key);
                switch (type)
                {
                    case CefValueType.Invalid:
                        break;
                    case CefValueType.Null:
                        result.SetAttributeValue(key, null);
                        break;
                    case CefValueType.Bool:
                        result.SetAttributeValue(key, value.GetBool(key));
                        break;
                    case CefValueType.Int:
                        result.SetAttributeValue(key, value.GetInt(key));
                        break;
                    case CefValueType.Double:
                        result.SetAttributeValue(key, value.GetDouble(key));
                        break;
                    case CefValueType.String:
                        result.SetAttributeValue(key, value.GetString(key));
                        break;
                    case CefValueType.Binary:
                        break;
                    case CefValueType.Dictionary:
                        result.Add(ToXElement(key, value.GetDictionary(key)));
                        break;
                    case CefValueType.List:
                        result.Add(ToXElement(key, value.GetList(key)));
                        break;
                    default:
                        break;
                }
            }
            return result;
        }

        static XElement ToXElement(string name, CefListValue value)
        {
            var result = new XElement(name);
            var count = value.Count;
            for (int i = 0; i < count; i++)
            {
                var type = value.GetValueType(i);
                switch (type)
                {
                    case CefValueType.Invalid:
                        break;
                    case CefValueType.Null:
                        result.Add(new XElement("item", null));
                        break;
                    case CefValueType.Bool:
                        result.Add(new XElement("item", value.GetBool(i)));
                        break;
                    case CefValueType.Int:
                        result.Add(new XElement("item", value.GetInt(i)));
                        break;
                    case CefValueType.Double:
                        result.Add(new XElement("item", value.GetDouble(i)));
                        break;
                    case CefValueType.String:
                        result.Add(new XElement("item", value.GetString(i)));
                        break;
                    case CefValueType.Binary:
                        break;
                    case CefValueType.Dictionary:
                        result.Add(ToXElement("item", value.GetDictionary(i)));
                        break;
                    case CefValueType.List:
                        result.Add(ToXElement("item", value.GetList(i)));
                        break;
                    default:
                        break;
                }
            }
            return result;
        }

        public bool TryReceive(out XElement value)
        {
            value = FReceivedData;
            FReceivedData = null;
            return value != null;
        }

        public void Reload()
        {
            FLoaded = false;
            FBrowser.Reload();
        }

        public XDocument CurrentDom
        {
            get { return FCurrentDom; }
        }

        public string CurrentUrl
        {
            get { return FCurrentUrl; }
        }

        public string CurrentError
        {
            get { return FErrorText; }
        }

        public string CurrentHTML
        {
            get { return FHtml; }
        }

        public Vector2 Size
        {
            get
            {
                var size = FSize;
                if (IsAutoWidth)
                    size.X = FDocumentSizeIsValid ? FDocumentSize.X : 0;
                if (IsAutoHeight)
                    size.Y = FDocumentSizeIsValid ? FDocumentSize.Y : 0;
                return size;
            }
            set
            {
                if (value != FSize)
                {
                    FSize = value;
                    if (!IsAutoSize)
                        FBrowserHost.WasResized();
                }
            }
        }

        public bool IsAutoWidth { get { return FSize.X <= 0; } }
        public bool IsAutoHeight { get { return FSize.Y <= 0; } }
        public bool IsAutoSize { get { return IsAutoWidth || IsAutoHeight; } }

        private bool FDocumentSizeIsValid;
        private Vector2 FDocumentSize;
        public Vector2 DocumentSize
        {
            get { return FDocumentSize; }
        }

        private double FZoomLevel;
        public double ZoomLevel
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

        private Vector2 FScrollTo;
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

        public IObservable<CefMouseEvent> MouseEvents
        {
            set
            {
                if (value != FMouseEvents)
                {
                    FMouseEvents = value;
                    FMouseSubscription = value.Subscribe(mouseEvent => FBrowserHost.SendMouseMoveEvent(mouseEvent, false));
                }
            }
        }
        IObservable<CefMouseEvent> FMouseEvents;
        IDisposable FMouseSubscription;

        public IObservable<CefKeyEvent> KeyboardEvents
        {
            set
            {
                if (value != FKeyboardEvents)
                {
                    FKeyboardEvents = value;
                    FKeyboardSubscription = value.Subscribe(keyEvent => FBrowserHost.SendKeyEvent(keyEvent));
                }
            }
        }
        IObservable<CefKeyEvent> FKeyboardEvents;
        IDisposable FKeyboardSubscription;

        private bool FIsLoading;
        public bool IsLoading
        {
            get
            {
                return FIsLoading || !FDomIsValid || (IsAutoSize && !FDocumentSizeIsValid);
            }
        }

        private bool FLoaded;
        public bool Loaded
        {
            get { return FLoaded; }
        }

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
                FCurrentDom = null;
                FDomIsValid = true;
                FErrorText = errorText;
            }
        }

        internal void OnLoadingStateChange(bool isLoading, bool canGoBack, bool canGoForward)
        {
            FIsLoading = isLoading;
            if (!isLoading)
            {
                using (var frame = FBrowser.GetMainFrame())
                {
                    FCurrentUrl = frame.Url;
                    UpdateDom(frame);
                    UpdateDocumentSize();
                }
                FLoaded = true;
                // HACK: Re-apply zooming level :/ - https://vvvv.org/forum/htmltexture-bug-with-zoomlevel
                FBrowserHost.SetZoomLevel(ZoomLevel);
            }
            else
                // Reset computed values like document size or DOM
                Reset();
        }

        internal void Paint(CefRectangle[] cefRects, IntPtr buffer, int stride, int width, int height)
        {
            // Do nothing if disabled
            if (!FEnabled) return;
            // If auto size is enabled ignore paint calls as long as document size is invalid
            if (IsAutoSize && !FDocumentSizeIsValid) return;
            var image = buffer.ToImage(stride * height, width, height, PixelFormat.B8G8R8A8);
            FImages.OnNext(image);
        }
    }
}
