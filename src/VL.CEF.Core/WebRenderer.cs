using System;
using Xilium.CefGlue;
using System.Globalization;
using System.Xml.Linq;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Core;
using MapMode = VL.Core.MapMode;
using System.Collections;
using VL.Lib.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace VL.CEF
{
    public interface IRenderHandler : IDisposable
    {
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
        private Spread<QueryHandler> FQueryHandlers;
        private readonly AutoResetEvent FBrowserAttachedEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent FBrowserDetachedEvent = new AutoResetEvent(false);
        private readonly TRenderHandler FRenderHandler;

        public WebRenderer(TRenderHandler renderHandler, bool sharedTextureEnabled)
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
            windowInfo.SharedTextureEnabled = sharedTextureEnabled;
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

        internal void OnQuery(CefFrame frame, CefListValue args)
        {
            var name = args.GetString(0);
            var id = args.GetInt(1);
            var argument = ToObject(args.GetValue(2));


            using var response = CefProcessMessage.Create("query-response");
            response.Arguments.SetString(0, name);
            response.Arguments.SetInt(1, id);

            var handler = QueryHandlers?.FirstOrDefault(q => q.Name == name);
            if (handler is null)
            {
                response.Arguments.SetString(2, $"No handler found for {name}.");
                response.Arguments.SetValue(3, ToValue(null));
            }
            else
            {
                try
                {
                    var result = handler.Invoke(argument);
                    response.Arguments.SetString(2, null);
                    response.Arguments.SetValue(3, ToValue(result));
                }
                catch (Exception e)
                {
                    response.Arguments.SetString(2, e.ToString());
                    response.Arguments.SetValue(3, ToValue(null));
                    RuntimeGraph.ReportException(e);
                }
            }
            
            frame.SendProcessMessage(CefProcessId.Renderer, response);

            static object ToObject(CefValue v)
            {
                switch (v.GetValueType())
                {
                    case CefValueType.Null:
                        return null;
                    case CefValueType.Bool:
                        return v.GetBool();
                    case CefValueType.Int:
                        return v.GetInt();
                    case CefValueType.Double:
                        return v.GetDouble();
                    case CefValueType.String:
                        return v.GetString();
                    case CefValueType.Binary:
                        return v.GetBinary().ToArray();
                    case CefValueType.Dictionary:
                        return ToDictionary(v.GetDictionary());
                    case CefValueType.List:
                        return ToSpread(v.GetList());
                    default:
                        throw new NotImplementedException();
                }

                static ImmutableDictionary<string, object> ToDictionary(CefDictionaryValue dict)
                {
                    var x = ImmutableDictionary.CreateBuilder<string, object>();
                    foreach (var key in dict.GetKeys())
                        x.Add(key, ToObject(dict.GetValue(key)));
                    return x.ToImmutable();
                }

                static Spread<object> ToSpread(CefListValue list)
                {
                    var x = new SpreadBuilder<object>(list.Count);
                    for (int i = 0; i < list.Count; i++)
                        x.Add(ToValue(list.GetValue(i)));
                    return x.ToSpread();
                }
            }

            static CefValue ToValue(object o)
            {
                var v = CefValue.Create();
                if (o is null)
                    v.SetNull();
                else if (o is bool b)
                    v.SetBool(b);
                else if (o is int i)
                    v.SetInt(i);
                else if (o is double d)
                    v.SetDouble(d);
                else if (o is float f)
                    v.SetDouble(f);
                else if (o is string s)
                    v.SetString(s);
                else if (o is byte[] data)
                    v.SetBinary(CefBinaryValue.Create(data));
                else if (o is IDictionary dict)
                    v.SetDictionary(ToDict(dict));
                else if (o is ICollection coll)
                    v.SetList(ToList(coll));
                return v;
            }

            static CefDictionaryValue ToDict(IDictionary dictionary)
            {
                var v = CefDictionaryValue.Create();
                foreach (DictionaryEntry x in dictionary)
                {
                    if (x.Key is string key)
                    {
                        var o = x.Value;
                        if (o is null)
                            v.SetNull(key);
                        else if (o is bool b)
                            v.SetBool(key, b);
                        else if (o is int i)
                            v.SetInt(key, i);
                        else if (o is double d)
                            v.SetDouble(key, d);
                        else if (o is float f)
                            v.SetDouble(key, f);
                        else if (o is string s)
                            v.SetString(key, s);
                        else if (o is byte[] data)
                            v.SetBinary(key, CefBinaryValue.Create(data));
                        else if (o is IDictionary dict)
                            v.SetDictionary(key, ToDict(dict));
                        else if (o is ICollection coll)
                            v.SetList(key, ToList(coll));
                    }
                }
                return v;
            }

            static CefListValue ToList(ICollection list)
            {
                var v = CefListValue.Create();
                var index = 0;
                foreach (var o in list)
                {
                    if (o is null)
                        v.SetNull(index);
                    else if (o is bool b)
                        v.SetBool(index, b);
                    else if (o is int i)
                        v.SetInt(index, i);
                    else if (o is double d)
                        v.SetDouble(index, d);
                    else if (o is float f)
                        v.SetDouble(index, f);
                    else if (o is string s)
                        v.SetString(index, s);
                    else if (o is byte[] data)
                        v.SetBinary(index, CefBinaryValue.Create(data));
                    else if (o is IDictionary dict)
                        v.SetDictionary(index, ToDict(dict));
                    else if (o is ICollection coll)
                        v.SetList(index, ToList(coll));

                    index++;
                }
                return v;
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
