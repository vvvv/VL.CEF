using System;
using Xilium.CefGlue;
using System.Diagnostics;
using Stride.Core.Mathematics;

namespace VL.CEF
{
    internal sealed class WebClient<T> : CefClient
        where T : IRenderHandler
    {
        public class RequestContextHandler : CefRequestContextHandler
        {
            protected override CefResourceRequestHandler GetResourceRequestHandler(CefBrowser browser, CefFrame frame, CefRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
            {
                return null;
            }
        }

        class ContextMenuHandler : CefContextMenuHandler
        {
            protected override void OnBeforeContextMenu(CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
            {
                model.Clear();
            }
        }

        class RenderHandler : CefRenderHandler
        {
            private readonly WebRenderer<T> renderer;
            
            public RenderHandler(WebRenderer<T> renderer)
            {
                this.renderer = renderer;
            }

            protected override bool GetRootScreenRect(CefBrowser browser, ref CefRectangle rect)
            {
                return false;
            }

            protected override void GetViewRect(CefBrowser browser, out CefRectangle rect)
            {
                var logicalSize = renderer.Size.DeviceToLogical(renderer.ScaleFactor);
                var width = Math.Max(1, (int)Math.Ceiling(logicalSize.X));
                var height = Math.Max(1, (int)Math.Ceiling(logicalSize.Y));
                rect = new CefRectangle(0, 0, width, height);
            }

            protected override bool GetScreenPoint(CefBrowser browser, int viewX, int viewY, ref int screenX, ref int screenY)
            {
                var viewPoint = new Vector2(viewX, viewY);
                var screenPoint = viewPoint.LogicalToDevice(renderer.ScaleFactor);
                screenX = (int)screenPoint.X;
                screenY = (int)screenPoint.Y;
                return true;
            }

            protected override void OnCursorChange(CefBrowser browser, IntPtr cursorHandle, CefCursorType type, CefCursorInfo customCursorInfo)
            {

            }

            protected override void OnPopupShow(CefBrowser browser, bool show)
            {
                base.OnPopupShow(browser, show);
            }

            protected override void OnPopupSize(CefBrowser browser, CefRectangle rect)
            {

            }

            protected override void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle)
            {
                renderer.OnAcceleratedPaint(browser, type, dirtyRects, sharedHandle);
            }

            protected override void OnPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr buffer, int width, int height)
            {
                renderer.OnPaint(browser, type, dirtyRects, buffer, width, height);
            }

            protected override bool GetScreenInfo(CefBrowser browser, CefScreenInfo screenInfo)
            {
                GetViewRect(browser, out var viewRect);

                screenInfo.DeviceScaleFactor = renderer.ScaleFactor;
                screenInfo.Rectangle = viewRect;
                screenInfo.AvailableRectangle = viewRect;

                return true;
            }

            protected override void OnScrollOffsetChanged(CefBrowser browser, double x, double y)
            {
                // Do not report the change as long as the renderer is busy loading content
                if (!renderer.IsLoading)
                    renderer.UpdateDocumentSize();
            }

            protected override CefAccessibilityHandler GetAccessibilityHandler()
            {
                return null;
            }

            protected override void OnImeCompositionRangeChanged(CefBrowser browser, CefRange selectedRange, CefRectangle[] characterBounds)
            {
                
            }

            protected override void OnVirtualKeyboardRequested(CefBrowser browser, CefTextInputMode inputMode)
            {
                renderer.OnVirtualKeyboardRequested(browser, inputMode);
            }
        }
        
        class LifeSpanHandler : CefLifeSpanHandler
        {
            private readonly WebClient<T> webClient;
            private readonly WebRenderer<T> renderer;
            
            public LifeSpanHandler(WebClient<T> webClient, WebRenderer<T> renderer)
            {
                this.webClient = webClient;
                this.renderer = renderer;
            }

            protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName, CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
            {
                renderer.LoadUrl(targetUrl);
                return true;
            }

            protected override void OnAfterCreated(CefBrowser browser)
            {
                renderer.Attach(browser);
                base.OnAfterCreated(browser);
            }
            
            protected override void OnBeforeClose(CefBrowser browser)
            {
                renderer.Detach();
                base.OnBeforeClose(browser);
            }
        }
        
        class LoadHandler : CefLoadHandler
        {
            private readonly WebRenderer<T> renderer;
            
            public LoadHandler(WebRenderer<T> renderer)
            {
                this.renderer = renderer;
            }

            protected override void OnLoadError(CefBrowser browser, CefFrame frame, CefErrorCode errorCode, string errorText, string failedUrl)
            {
                renderer.OnLoadError(frame, errorCode, failedUrl, errorText);
                base.OnLoadError(browser, frame, errorCode, errorText, failedUrl);
            }

            protected override void OnLoadingStateChange(CefBrowser browser, bool isLoading, bool canGoBack, bool canGoForward)
            {
                renderer.OnLoadingStateChange(isLoading, canGoBack, canGoForward);
                base.OnLoadingStateChange(browser, isLoading, canGoBack, canGoForward);
            }
        }

        class KeyboardHandler : CefKeyboardHandler
        {
            protected override bool OnKeyEvent(CefBrowser browser, CefKeyEvent keyEvent, IntPtr osEvent)
            {
                return base.OnKeyEvent(browser, keyEvent, osEvent);
            }

            protected override bool OnPreKeyEvent(CefBrowser browser, CefKeyEvent keyEvent, IntPtr os_event, out bool isKeyboardShortcut)
            {
                return base.OnPreKeyEvent(browser, keyEvent, os_event, out isKeyboardShortcut);
            }
        }

        class DisplayHandler : CefDisplayHandler
        {
            private readonly WebRenderer<T> renderer;

            public DisplayHandler(WebRenderer<T> renderer)
            {
                this.renderer = renderer;
            }

            protected override bool OnConsoleMessage(CefBrowser browser, CefLogSeverity level, string message, string source, int line)
            {
                var m = string.Format("{0} ({1}:{2})", message, source, line);
                switch (level)
                {
                    case CefLogSeverity.Default:
                    case CefLogSeverity.Verbose:
                    case CefLogSeverity.Info:
                        Trace.TraceInformation(m);
                        break;
                    case CefLogSeverity.Warning:
                        Trace.TraceWarning(m);
                        break;
                    case CefLogSeverity.Error:
                    case CefLogSeverity.Fatal:
                        Trace.TraceError(m);
                        break;
                }
                return base.OnConsoleMessage(browser, level, message, source, line);
            }
        }

        private readonly WebRenderer<T> renderer;
        private readonly CefRenderHandler renderHandler;
        private readonly CefLifeSpanHandler lifeSpanHandler;
        private readonly CefLoadHandler loadHandler;
        private readonly CefKeyboardHandler keyboardHandler;
        private readonly CefDisplayHandler displayHandler;
        private readonly CefContextMenuHandler contextMenuHandler;
        
        public WebClient(WebRenderer<T> renderer)
        {
            this.renderer = renderer;
            renderHandler = new RenderHandler(renderer);
            lifeSpanHandler = new LifeSpanHandler(this, renderer);
            loadHandler = new LoadHandler(renderer);
            keyboardHandler = new KeyboardHandler();
            displayHandler = new DisplayHandler(renderer);
            contextMenuHandler = new ContextMenuHandler();
        }

        protected override CefContextMenuHandler GetContextMenuHandler()
        {
            return contextMenuHandler;
        }

        protected override CefDisplayHandler GetDisplayHandler()
        {
            return displayHandler;
        }
        
        protected override CefFocusHandler GetFocusHandler()
        {
            return base.GetFocusHandler();
        }
        
        protected override CefJSDialogHandler GetJSDialogHandler()
        {
            return base.GetJSDialogHandler();
        }
        
        protected override CefKeyboardHandler GetKeyboardHandler()
        {
            return keyboardHandler;
        }
        
        protected override CefLifeSpanHandler GetLifeSpanHandler()
        {
            return lifeSpanHandler;
        }
        
        protected override CefLoadHandler GetLoadHandler()
        {
            return loadHandler;
        }

        protected override CefRequestHandler GetRequestHandler()
        {
            return null;
        }
        
        protected override CefRenderHandler GetRenderHandler()
        {
            return renderHandler;
        }

        protected override bool OnProcessMessageReceived(CefBrowser browser, CefFrame frame, CefProcessId sourceProcess, CefProcessMessage message)
        {
            switch (message.Name)
            {
                case "document-size-response":
                    if (frame != null)
                    {
                        var arguments = message.Arguments;
                        var width = arguments.GetInt(0);
                        var height = arguments.GetInt(1);
                        renderer.OnDocumentSize(frame, width, height);
                    }
                    return true;
                case "query-request":
                    if (frame != null)
                    {
                        var arguments = message.Arguments;
                        renderer.OnQuery(frame, arguments);
                    }
                    return true;
                default:
                    break;
            }
            return base.OnProcessMessageReceived(browser, frame, sourceProcess, message);
        }
    }
}
