using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Text;
using System.Threading.Tasks;
using Xilium.CefGlue;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace VL.CEF
{
    partial class StrideRenderHandler
    {
        IDisposable SubscribeToInputSource(IInputSource inputSource, RenderDrawContext context)
        {
            if (inputSource is null)
                return Disposable.Empty;

            var inputManager = context.RenderContext.Services.GetService<InputManager>();
            if (inputManager is null)
                return Disposable.Empty;

            var mouseButtonListener = NewMouseButtonListener(inputSource);
            inputManager.AddListener(mouseButtonListener);
            var mouseWheelListener = NewMouseWheelListener(inputSource);
            inputManager.AddListener(mouseWheelListener);
            var pointerListener = NewPointerListener(inputSource);
            inputManager.AddListener(pointerListener);
            var keyListener = NewKeyListener(inputSource);
            inputManager.AddListener(keyListener);
            var textListener = NewTextInputListener(inputSource);
            inputManager.AddListener(textListener);

            var subscription = Disposable.Create(() =>
            {
                inputManager.RemoveListener(mouseButtonListener);
                inputManager.RemoveListener(mouseWheelListener);
                inputManager.RemoveListener(pointerListener);
                inputManager.RemoveListener(keyListener);
                inputManager.RemoveListener(textListener);
            });

            return subscription;
        }

        private IInputEventListener NewPointerListener(IInputSource inputSource)
        {
            return new AnonymousEventListener<PointerEvent>(e =>
            {
                if (e.Device.Source != inputSource)
                    return;

                var position = e.AbsolutePosition.DeviceToLogical(webRenderer.ScaleFactor);

                webRenderer.BrowserHost.SendTouchEvent(new CefTouchEvent()
                {
                    Id = e.PointerId,
                    X = position.X,
                    Y = position.Y,
                    Type = ToCefTouchEventType(e.EventType),
                });

                if (e.EventType == PointerEventType.Moved && e.Device is IMouseDevice mouse)
                {
                    var mouseEvent = ToMouseEvent(mouse);
                    webRenderer.BrowserHost.SendMouseMoveEvent(mouseEvent, mouseLeave: false);
                }
            });
        }

        private IInputEventListener NewMouseButtonListener(IInputSource inputSource)
        {
            return new AnonymousEventListener<MouseButtonEvent>(e =>
            {
                if (e.Device.Source != inputSource)
                    return;

                var mouseEvent = ToMouseEvent(e.Mouse);
                var mouseDevice = e.Device as IMouseDevice;
                if (e.IsDown)
                    webRenderer.BrowserHost.SendMouseClickEvent(mouseEvent, ToCefMouseButton(e.Button), mouseUp: false, clickCount: 1);
                else
                    webRenderer.BrowserHost.SendMouseClickEvent(mouseEvent, ToCefMouseButton(e.Button), mouseUp: true, clickCount: 1);
            });
        }

        private IInputEventListener NewMouseWheelListener(IInputSource inputSource)
        {
            return new AnonymousEventListener<MouseWheelEvent>(e =>
            {
                if (e.Device.Source != inputSource)
                    return;
                var mouseEvent = ToMouseEvent(e.Mouse);
                webRenderer.BrowserHost.SendMouseWheelEvent(mouseEvent, 0, (int)e.WheelDelta * 120);
            });
        }

        private IInputEventListener NewKeyListener(IInputSource inputSource)
        {
            return new AnonymousEventListener<KeyEvent>(e =>
            {
                if (e.Device.Source != inputSource)
                    return;

                var key = ToWinFormsKeys(e.Key);
                var cefEvent = new CefKeyEvent()
                {
                    WindowsKeyCode = (int)key,
                    NativeKeyCode = (int)key
                };
                if (e.IsDown)
                    cefEvent.EventType = CefKeyEventType.KeyDown;
                else
                    cefEvent.EventType = CefKeyEventType.KeyUp;

                webRenderer.BrowserHost.SendKeyEvent(cefEvent);
            });
        }

        private IInputEventListener NewTextInputListener(IInputSource inputSource)
        {
            return new AnonymousEventListener<TextInputEvent>(e =>
            {
                if (e.Device.Source != inputSource)
                    return;

                if (e.Type == TextInputEventType.Input)
                {
                    foreach (var c in e.Text)
                    {
                        webRenderer.BrowserHost.SendKeyEvent(new CefKeyEvent()
                        {
                            EventType = CefKeyEventType.Char,
                            Character = c
                        });
                    }
                }
            });
        }

        CefMouseEvent ToMouseEvent(IMouseDevice mouse)
        {
            var position = (mouse.Position * mouse.SurfaceSize).DeviceToLogical(webRenderer.ScaleFactor);
            return new CefMouseEvent((int)position.X, (int)position.Y, GetMouseModifiers(mouse));
        }

        CefEventFlags GetMouseModifiers(IMouseDevice mouse)
        {
            var result = CefEventFlags.None;

            var buttons = mouse.PressedButtons;
            foreach (var button in mouse.DownButtons)
            {
                switch (button)
                {
                    case MouseButton.Left:
                        result |= CefEventFlags.LeftMouseButton;
                        break;
                    case MouseButton.Middle:
                        result |= CefEventFlags.MiddleMouseButton;
                        break;
                    case MouseButton.Right:
                        result |= CefEventFlags.RightMouseButton;
                        break;
                    case MouseButton.Extended1:
                        break;
                    case MouseButton.Extended2:
                        break;
                    default:
                        break;
                }
            }

            return result;
        }

        static CefMouseButtonType ToCefMouseButton(MouseButton button)
        {
            switch (button)
            {
                case MouseButton.Left:
                    return CefMouseButtonType.Left;
                case MouseButton.Middle:
                    return CefMouseButtonType.Middle;
                case MouseButton.Right:
                    return CefMouseButtonType.Right;
                default:
                    return default;
            }
        }

        static CefTouchEventType ToCefTouchEventType(PointerEventType pointerEventType)
        {
            switch (pointerEventType)
            {
                case PointerEventType.Pressed:
                    return CefTouchEventType.Pressed;
                case PointerEventType.Moved:
                    return CefTouchEventType.Moved;
                case PointerEventType.Released:
                    return CefTouchEventType.Released;
                case PointerEventType.Canceled:
                    return CefTouchEventType.Cancelled;
                default:
                    return default;
            }
        }

        static System.Windows.Forms.MouseButtons ToWinFormsButton(MouseButton mouseButton)
        {
            switch (mouseButton)
            {
                case MouseButton.Left:
                    return System.Windows.Forms.MouseButtons.Left;
                case MouseButton.Middle:
                    return System.Windows.Forms.MouseButtons.Middle;
                case MouseButton.Right:
                    return System.Windows.Forms.MouseButtons.Right;
                case MouseButton.Extended1:
                    return System.Windows.Forms.MouseButtons.XButton1;
                case MouseButton.Extended2:
                    return System.Windows.Forms.MouseButtons.XButton2;
                default:
                    return System.Windows.Forms.MouseButtons.None;
            }
        }

        static WinFormsKeys ToWinFormsKeys(Keys keys)
        {
            if (WinKeys.ReverseMapKeys.TryGetValue(keys, out var value))
                return value;
            else
                return WinFormsKeys.None;
        }

        sealed class AnonymousEventListener<T> : IInputEventListener<T>
            where T : InputEvent
        {
            readonly Action<T> ProcessEventAction;

            public AnonymousEventListener(Action<T> processEventAction)
            {
                ProcessEventAction = processEventAction;
            }

            public void ProcessEvent(T inputEvent)
            {
                ProcessEventAction(inputEvent);
            }
        }

        internal static class WinKeys
        {
            /// <summary>
            /// Map between Winform keys and Stride keys.
            /// </summary>
            internal static readonly Dictionary<WinFormsKeys, Keys> MapKeys = NewMapKeys();

            /// <summary>
            /// Map between Stride keys and Winforms keys.
            /// </summary>
            internal static readonly Dictionary<Keys, WinFormsKeys> ReverseMapKeys = NewMapKeys().ToDictionary(e => e.Value, e => e.Key);


            private static Dictionary<WinFormsKeys, Keys> NewMapKeys()
            {
                var map = new Dictionary<WinFormsKeys, Keys>(200);
                map[WinFormsKeys.None] = Keys.None;
                map[WinFormsKeys.Cancel] = Keys.Cancel;
                map[WinFormsKeys.Back] = Keys.Back;
                map[WinFormsKeys.Tab] = Keys.Tab;
                map[WinFormsKeys.LineFeed] = Keys.LineFeed;
                map[WinFormsKeys.Clear] = Keys.Clear;
                map[WinFormsKeys.Enter] = Keys.Enter;
                map[WinFormsKeys.Return] = Keys.Return;
                map[WinFormsKeys.Pause] = Keys.Pause;
                map[WinFormsKeys.Capital] = Keys.Capital;
                map[WinFormsKeys.CapsLock] = Keys.CapsLock;
                map[WinFormsKeys.HangulMode] = Keys.HangulMode;
                map[WinFormsKeys.KanaMode] = Keys.KanaMode;
                map[WinFormsKeys.JunjaMode] = Keys.JunjaMode;
                map[WinFormsKeys.FinalMode] = Keys.FinalMode;
                map[WinFormsKeys.HanjaMode] = Keys.HanjaMode;
                map[WinFormsKeys.KanjiMode] = Keys.KanjiMode;
                map[WinFormsKeys.Escape] = Keys.Escape;
                map[WinFormsKeys.IMEConvert] = Keys.ImeConvert;
                map[WinFormsKeys.IMENonconvert] = Keys.ImeNonConvert;
                map[WinFormsKeys.IMEAccept] = Keys.ImeAccept;
                map[WinFormsKeys.IMEModeChange] = Keys.ImeModeChange;
                map[WinFormsKeys.Space] = Keys.Space;
                map[WinFormsKeys.PageUp] = Keys.PageUp;
                map[WinFormsKeys.Prior] = Keys.Prior;
                map[WinFormsKeys.Next] = Keys.Next;
                map[WinFormsKeys.PageDown] = Keys.PageDown;
                map[WinFormsKeys.End] = Keys.End;
                map[WinFormsKeys.Home] = Keys.Home;
                map[WinFormsKeys.Left] = Keys.Left;
                map[WinFormsKeys.Up] = Keys.Up;
                map[WinFormsKeys.Right] = Keys.Right;
                map[WinFormsKeys.Down] = Keys.Down;
                map[WinFormsKeys.Select] = Keys.Select;
                map[WinFormsKeys.Print] = Keys.Print;
                map[WinFormsKeys.Execute] = Keys.Execute;
                map[WinFormsKeys.PrintScreen] = Keys.PrintScreen;
                map[WinFormsKeys.Snapshot] = Keys.Snapshot;
                map[WinFormsKeys.Insert] = Keys.Insert;
                map[WinFormsKeys.Delete] = Keys.Delete;
                map[WinFormsKeys.Help] = Keys.Help;
                map[WinFormsKeys.D0] = Keys.D0;
                map[WinFormsKeys.D1] = Keys.D1;
                map[WinFormsKeys.D2] = Keys.D2;
                map[WinFormsKeys.D3] = Keys.D3;
                map[WinFormsKeys.D4] = Keys.D4;
                map[WinFormsKeys.D5] = Keys.D5;
                map[WinFormsKeys.D6] = Keys.D6;
                map[WinFormsKeys.D7] = Keys.D7;
                map[WinFormsKeys.D8] = Keys.D8;
                map[WinFormsKeys.D9] = Keys.D9;
                map[WinFormsKeys.A] = Keys.A;
                map[WinFormsKeys.B] = Keys.B;
                map[WinFormsKeys.C] = Keys.C;
                map[WinFormsKeys.D] = Keys.D;
                map[WinFormsKeys.E] = Keys.E;
                map[WinFormsKeys.F] = Keys.F;
                map[WinFormsKeys.G] = Keys.G;
                map[WinFormsKeys.H] = Keys.H;
                map[WinFormsKeys.I] = Keys.I;
                map[WinFormsKeys.J] = Keys.J;
                map[WinFormsKeys.K] = Keys.K;
                map[WinFormsKeys.L] = Keys.L;
                map[WinFormsKeys.M] = Keys.M;
                map[WinFormsKeys.N] = Keys.N;
                map[WinFormsKeys.O] = Keys.O;
                map[WinFormsKeys.P] = Keys.P;
                map[WinFormsKeys.Q] = Keys.Q;
                map[WinFormsKeys.R] = Keys.R;
                map[WinFormsKeys.S] = Keys.S;
                map[WinFormsKeys.T] = Keys.T;
                map[WinFormsKeys.U] = Keys.U;
                map[WinFormsKeys.V] = Keys.V;
                map[WinFormsKeys.W] = Keys.W;
                map[WinFormsKeys.X] = Keys.X;
                map[WinFormsKeys.Y] = Keys.Y;
                map[WinFormsKeys.Z] = Keys.Z;
                map[WinFormsKeys.LWin] = Keys.LeftWin;
                map[WinFormsKeys.RWin] = Keys.RightWin;
                map[WinFormsKeys.Apps] = Keys.Apps;
                map[WinFormsKeys.Sleep] = Keys.Sleep;
                map[WinFormsKeys.NumPad0] = Keys.NumPad0;
                map[WinFormsKeys.NumPad1] = Keys.NumPad1;
                map[WinFormsKeys.NumPad2] = Keys.NumPad2;
                map[WinFormsKeys.NumPad3] = Keys.NumPad3;
                map[WinFormsKeys.NumPad4] = Keys.NumPad4;
                map[WinFormsKeys.NumPad5] = Keys.NumPad5;
                map[WinFormsKeys.NumPad6] = Keys.NumPad6;
                map[WinFormsKeys.NumPad7] = Keys.NumPad7;
                map[WinFormsKeys.NumPad8] = Keys.NumPad8;
                map[WinFormsKeys.NumPad9] = Keys.NumPad9;
                map[WinFormsKeys.Multiply] = Keys.Multiply;
                map[WinFormsKeys.Add] = Keys.Add;
                map[WinFormsKeys.Separator] = Keys.Separator;
                map[WinFormsKeys.Subtract] = Keys.Subtract;
                map[WinFormsKeys.Decimal] = Keys.Decimal;
                map[WinFormsKeys.Divide] = Keys.Divide;
                map[WinFormsKeys.F1] = Keys.F1;
                map[WinFormsKeys.F2] = Keys.F2;
                map[WinFormsKeys.F3] = Keys.F3;
                map[WinFormsKeys.F4] = Keys.F4;
                map[WinFormsKeys.F5] = Keys.F5;
                map[WinFormsKeys.F6] = Keys.F6;
                map[WinFormsKeys.F7] = Keys.F7;
                map[WinFormsKeys.F8] = Keys.F8;
                map[WinFormsKeys.F9] = Keys.F9;
                map[WinFormsKeys.F10] = Keys.F10;
                map[WinFormsKeys.F11] = Keys.F11;
                map[WinFormsKeys.F12] = Keys.F12;
                map[WinFormsKeys.F13] = Keys.F13;
                map[WinFormsKeys.F14] = Keys.F14;
                map[WinFormsKeys.F15] = Keys.F15;
                map[WinFormsKeys.F16] = Keys.F16;
                map[WinFormsKeys.F17] = Keys.F17;
                map[WinFormsKeys.F18] = Keys.F18;
                map[WinFormsKeys.F19] = Keys.F19;
                map[WinFormsKeys.F20] = Keys.F20;
                map[WinFormsKeys.F21] = Keys.F21;
                map[WinFormsKeys.F22] = Keys.F22;
                map[WinFormsKeys.F23] = Keys.F23;
                map[WinFormsKeys.F24] = Keys.F24;
                map[WinFormsKeys.NumLock] = Keys.NumLock;
                map[WinFormsKeys.Scroll] = Keys.Scroll;
                map[WinFormsKeys.LShiftKey] = Keys.LeftShift;
                map[WinFormsKeys.RShiftKey] = Keys.RightShift;
                map[WinFormsKeys.LControlKey] = Keys.LeftCtrl;
                map[WinFormsKeys.RControlKey] = Keys.RightCtrl;
                map[WinFormsKeys.LMenu] = Keys.LeftAlt;
                map[WinFormsKeys.RMenu] = Keys.RightAlt;
                map[WinFormsKeys.BrowserBack] = Keys.BrowserBack;
                map[WinFormsKeys.BrowserForward] = Keys.BrowserForward;
                map[WinFormsKeys.BrowserRefresh] = Keys.BrowserRefresh;
                map[WinFormsKeys.BrowserStop] = Keys.BrowserStop;
                map[WinFormsKeys.BrowserSearch] = Keys.BrowserSearch;
                map[WinFormsKeys.BrowserFavorites] = Keys.BrowserFavorites;
                map[WinFormsKeys.BrowserHome] = Keys.BrowserHome;
                map[WinFormsKeys.VolumeMute] = Keys.VolumeMute;
                map[WinFormsKeys.VolumeDown] = Keys.VolumeDown;
                map[WinFormsKeys.VolumeUp] = Keys.VolumeUp;
                map[WinFormsKeys.MediaNextTrack] = Keys.MediaNextTrack;
                map[WinFormsKeys.MediaPreviousTrack] = Keys.MediaPreviousTrack;
                map[WinFormsKeys.MediaStop] = Keys.MediaStop;
                map[WinFormsKeys.MediaPlayPause] = Keys.MediaPlayPause;
                map[WinFormsKeys.LaunchMail] = Keys.LaunchMail;
                map[WinFormsKeys.SelectMedia] = Keys.SelectMedia;
                map[WinFormsKeys.LaunchApplication1] = Keys.LaunchApplication1;
                map[WinFormsKeys.LaunchApplication2] = Keys.LaunchApplication2;
                map[WinFormsKeys.Oem1] = Keys.Oem1;
                map[WinFormsKeys.OemSemicolon] = Keys.OemSemicolon;
                map[WinFormsKeys.Oemplus] = Keys.OemPlus;
                map[WinFormsKeys.Oemcomma] = Keys.OemComma;
                map[WinFormsKeys.OemMinus] = Keys.OemMinus;
                map[WinFormsKeys.OemPeriod] = Keys.OemPeriod;
                map[WinFormsKeys.Oem2] = Keys.Oem2;
                map[WinFormsKeys.OemQuestion] = Keys.OemQuestion;
                map[WinFormsKeys.Oem3] = Keys.Oem3;
                map[WinFormsKeys.Oemtilde] = Keys.OemTilde;
                map[WinFormsKeys.Oem4] = Keys.Oem4;
                map[WinFormsKeys.OemOpenBrackets] = Keys.OemOpenBrackets;
                map[WinFormsKeys.Oem5] = Keys.Oem5;
                map[WinFormsKeys.OemPipe] = Keys.OemPipe;
                map[WinFormsKeys.Oem6] = Keys.Oem6;
                map[WinFormsKeys.OemCloseBrackets] = Keys.OemCloseBrackets;
                map[WinFormsKeys.Oem7] = Keys.Oem7;
                map[WinFormsKeys.OemQuotes] = Keys.OemQuotes;
                map[WinFormsKeys.Oem8] = Keys.Oem8;
                map[WinFormsKeys.Oem102] = Keys.Oem102;
                map[WinFormsKeys.OemBackslash] = Keys.OemBackslash;
                map[WinFormsKeys.Attn] = Keys.Attn;
                map[WinFormsKeys.Crsel] = Keys.CrSel;
                map[WinFormsKeys.Exsel] = Keys.ExSel;
                map[WinFormsKeys.EraseEof] = Keys.EraseEof;
                map[WinFormsKeys.Play] = Keys.Play;
                map[WinFormsKeys.Zoom] = Keys.Zoom;
                map[WinFormsKeys.NoName] = Keys.NoName;
                map[WinFormsKeys.Pa1] = Keys.Pa1;
                map[WinFormsKeys.OemClear] = Keys.OemClear;
                return map;
            }
        }
    }
}
