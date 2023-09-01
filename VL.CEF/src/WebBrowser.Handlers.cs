using System;
using Xilium.CefGlue;
using System.Diagnostics;
using Stride.Core.Mathematics;
using System.Collections;
using VL.Lib.Collections;
using System.Collections.Immutable;
using VL.Core;

namespace VL.CEF
{
    partial class WebBrowser
    {
        class RequestContextHandler : CefRequestContextHandler
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
            private readonly WebBrowser renderer;
            
            public RenderHandler(WebBrowser renderer)
            {
                this.renderer = renderer;
            }

            protected override bool GetRootScreenRect(CefBrowser browser, ref CefRectangle rect)
            {
                return false;
            }

            protected override void GetViewRect(CefBrowser browser, out CefRectangle rect)
            {
                var logicalSize = renderer.ComittedSize.DeviceToLogical(renderer.ScaleFactor);
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

            protected override void OnPopupShow(CefBrowser browser, bool show)
            {
                base.OnPopupShow(browser, show);
            }

            protected override void OnPopupSize(CefBrowser browser, CefRectangle rect)
            {

            }

            protected override void OnAcceleratedPaint2(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle, int newTexture)
            {
                if (renderer.Enabled && type == CefPaintElementType.View)
                    renderer.AcceleratedPaint2?.Invoke(type, dirtyRects, sharedHandle, newTexture);
            }

            protected override void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr sharedHandle)
            {
                if (renderer.Enabled && type == CefPaintElementType.View)
                    renderer.AcceleratedPaint?.Invoke(type, dirtyRects, sharedHandle);
            }

            protected override void OnPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr buffer, int width, int height)
            {
                if (renderer.Enabled && type == CefPaintElementType.View)
                    renderer.Paint?.Invoke(type, dirtyRects, buffer, width, height);
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
                if (renderer.Enabled)
                    renderer.FVirtualKeyboardRequests.OnNext(inputMode);
            }
        }
        
        class LifeSpanHandler : CefLifeSpanHandler
        {
            private readonly WebBrowser renderer;
            
            public LifeSpanHandler(WebBrowser renderer)
            {
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
            private readonly WebBrowser renderer;
            
            public LoadHandler(WebBrowser renderer)
            {
                this.renderer = renderer;
            }

            protected override void OnLoadError(CefBrowser browser, CefFrame frame, CefErrorCode errorCode, string errorText, string failedUrl)
            {
                if (frame.IsMain)
                {
                    renderer.ErrorText = errorText;
                }
                base.OnLoadError(browser, frame, errorCode, errorText, failedUrl);
            }

            protected override void OnLoadingStateChange(CefBrowser browser, bool isLoading, bool canGoBack, bool canGoForward)
            {
                renderer.IsLoading = isLoading;
                if (!isLoading)
                {
                    renderer.UpdateDocumentSize();
                    renderer.Loaded = true;
                    // HACK: Re-apply zooming level :/ - https://vvvv.org/forum/htmltexture-bug-with-zoomlevel
                    renderer.FBrowserHost.SetZoomLevel(renderer.ZoomLevel);
                }
                else
                    // Reset computed values like document size or DOM
                    renderer.Reset();

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
            private readonly WebBrowser renderer;

            public DisplayHandler(WebBrowser renderer)
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

            protected override bool OnCursorChange(CefBrowser browser, IntPtr cursorHandle, CefCursorType type, CefCursorInfo customCursorInfo)
            {
                // Returning true marks this as handled which is equivalent to the old 
                // SetMouseCursorChangeDisabled in WebBrowser::Attach
                // https://github.com/cefsharp/CefSharp/issues/3275
                return true;
            }
        }

        /// <inheritdoc />
        protected override CefContextMenuHandler GetContextMenuHandler()
        {
            return contextMenuHandler;
        }

        /// <inheritdoc />
        protected override CefDisplayHandler GetDisplayHandler()
        {
            return displayHandler;
        }

        /// <inheritdoc />
        protected override CefFocusHandler GetFocusHandler()
        {
            return base.GetFocusHandler();
        }

        /// <inheritdoc />
        protected override CefJSDialogHandler GetJSDialogHandler()
        {
            return base.GetJSDialogHandler();
        }

        /// <inheritdoc />
        protected override CefKeyboardHandler GetKeyboardHandler()
        {
            return keyboardHandler;
        }

        /// <inheritdoc />
        protected override CefLifeSpanHandler GetLifeSpanHandler()
        {
            return lifeSpanHandler;
        }

        /// <inheritdoc />
        protected override CefLoadHandler GetLoadHandler()
        {
            return loadHandler;
        }

        /// <inheritdoc />
        protected override CefRequestHandler GetRequestHandler()
        {
            return null;
        }

        /// <inheritdoc />
        protected override CefRenderHandler GetRenderHandler()
        {
            return renderHandler;
        }

        /// <inheritdoc />
        protected override bool OnProcessMessageReceived(CefBrowser browser, CefFrame frame, CefProcessId sourceProcess, CefProcessMessage message)
        {
            switch (message.Name)
            {
                case "document-size-response":
                    if (frame != null && frame.IsMain)
                    {
                        var arguments = message.Arguments;
                        var width = arguments.GetInt(0);
                        var height = arguments.GetInt(1);
                        DocumentSize = new Vector2(width, height);
                    }
                    return true;
                case "query-request":
                    if (frame != null)
                    {
                        var arguments = message.Arguments;
                        OnQuery(frame, arguments);
                    }
                    return true;
                default:
                    break;
            }
            return base.OnProcessMessageReceived(browser, frame, sourceProcess, message);

            void OnQuery(CefFrame frame, CefListValue args)
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
        }
    }
}
