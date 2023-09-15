using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using Xilium.CefGlue;
using VLKeys = VL.Lib.IO.Keys;
using System.Diagnostics;
using VL.Lib.IO;
using StrideKeys = Stride.Input.Keys;

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

                if (e.Device is IMouseDevice mouse)
                {
                    if (e.EventType == PointerEventType.Moved)
                    {
                        var mouseEvent = ToMouseEvent(mouse);
                        browser.BrowserHost.SendMouseMoveEvent(mouseEvent, mouseLeave: false);
                    }
                }
                else
                {
                    var position = e.AbsolutePosition.DeviceToLogical(browser.ScaleFactor);

                    browser.BrowserHost.SendTouchEvent(new CefTouchEvent()
                    {
                        Id = e.PointerId,
                        X = position.X,
                        Y = position.Y,
                        Type = ToCefTouchEventType(e.EventType),
                        Modifiers = GetModifiers(GetKeyboard(inputSource)) | GetModifiers(GetMouse(inputSource))
                    });
                }
            });
        }

        int clickCount = 1;
        MouseButton? lastButton;
        Vector2 lastPosition;
        Stopwatch stopwatch = Stopwatch.StartNew();

        private IInputEventListener NewMouseButtonListener(IInputSource inputSource)
        {
            return new AnonymousEventListener<MouseButtonEvent>(e =>
            {
                if (e.Device.Source != inputSource)
                    return;

                var mouseDevice = e.Device as IMouseDevice;
                var mouseEvent = ToMouseEvent(e.Mouse);

                var position = mouseDevice.Position * mouseDevice.SurfaceSize;
                var delta = lastPosition - position;
                var deltaTime = stopwatch.ElapsedMilliseconds;
                stopwatch.Restart();

                if (e.IsDown)
                {
                    if (e.Button == lastButton && 
                        Math.Abs(delta.X) < Mouse.DoubleClickSize.Width / 2 &&
                        Math.Abs(delta.Y) < Mouse.DoubleClickSize.Height / 2 && 
                        deltaTime < Mouse.DoubleClickTime)
                    {
                        clickCount++;
                    }
                    else
                    {
                        clickCount = 1;
                    }
                }
                else if (e.Button != lastButton)
                {
                    clickCount = 1;
                }

                lastButton = e.Button;
                lastPosition = position;

                if (e.IsDown)
                    browser.BrowserHost.SendMouseClickEvent(mouseEvent, ToCefMouseButton(e.Button), mouseUp: false, clickCount: clickCount);
                else
                    browser.BrowserHost.SendMouseClickEvent(mouseEvent, ToCefMouseButton(e.Button), mouseUp: true, clickCount: clickCount);
            });
        }

        private IInputEventListener NewMouseWheelListener(IInputSource inputSource)
        {
            return new AnonymousEventListener<MouseWheelEvent>(e =>
            {
                if (e.Device.Source != inputSource)
                    return;
                var mouseEvent = ToMouseEvent(e.Mouse);
                browser.BrowserHost.SendMouseWheelEvent(mouseEvent, 0, (int)e.WheelDelta * 120);
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
                    NativeKeyCode = (int)key,
                    Modifiers = GetModifiers(e.Keyboard) | GetModifiers(GetMouse(inputSource))
                };
                if (e.IsDown)
                    cefEvent.EventType = CefKeyEventType.KeyDown;
                else
                    cefEvent.EventType = CefKeyEventType.KeyUp;

                browser.BrowserHost.SendKeyEvent(cefEvent);
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
                        browser.BrowserHost.SendKeyEvent(new CefKeyEvent()
                        {
                            EventType = CefKeyEventType.Char,
                            Character = c,
                            UnmodifiedCharacter = c,
                            WindowsKeyCode = (int)c,
                            NativeKeyCode = (int)c,
                            Modifiers = GetModifiers(e.Device as IKeyboardDevice) | GetModifiers(GetMouse(inputSource))
                        });
                    }
                }
            });
        }

        CefMouseEvent ToMouseEvent(IMouseDevice mouse)
        {
            var position = (mouse.Position * mouse.SurfaceSize).DeviceToLogical(browser.ScaleFactor);
            return new CefMouseEvent((int)position.X, (int)position.Y, GetModifiers(mouse) | GetModifiers(GetKeyboard(mouse.Source)));
        }

        IMouseDevice GetMouse(IInputSource inputSource)
        {
            foreach (var entry in inputSource.Devices)
                if (entry.Value is IMouseDevice m)
                    return m;
            return null;
        }

        IKeyboardDevice GetKeyboard(IInputSource inputSource)
        {
            foreach (var entry in inputSource.Devices)
                if (entry.Value is IKeyboardDevice k)
                    return k;
            return null;
        }

        CefEventFlags GetModifiers(IMouseDevice mouse)
        {
            var result = CefEventFlags.None;
            if (mouse is null)
                return result;

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
                }
            }
            return result;
        }


        static CefEventFlags GetModifiers(IKeyboardDevice keyboard)
        {
            var result = CefEventFlags.None;
            if (keyboard is null)
                return result;

            foreach (var key in keyboard.DownKeys)
            {
                switch (key)
                {
                    case StrideKeys.LeftShift:
                        result |= CefEventFlags.ShiftDown | CefEventFlags.IsLeft;
                        break;
                    case StrideKeys.RightShift:
                        result |= CefEventFlags.ShiftDown | CefEventFlags.IsRight;
                        break;
                    case StrideKeys.LeftCtrl:
                        result |= CefEventFlags.ControlDown | CefEventFlags.IsLeft;
                        break;
                    case StrideKeys.RightCtrl:
                        result |= CefEventFlags.ControlDown | CefEventFlags.IsRight;
                        break;
                    case StrideKeys.LeftAlt:
                        result |= CefEventFlags.AltDown | CefEventFlags.IsLeft;
                        break;
                    case StrideKeys.RightAlt:
                        result |= CefEventFlags.AltDown | CefEventFlags.IsRight;
                        break;
                    case StrideKeys.LeftWin:
                        result |= CefEventFlags.CommandDown | CefEventFlags.IsLeft;
                        break;
                    case StrideKeys.RightWin:
                        result |= CefEventFlags.CommandDown | CefEventFlags.IsRight;
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

        static VLKeys ToWinFormsKeys(StrideKeys keys)
        {
            if (WinKeys.ReverseMapKeys.TryGetValue(keys, out var value))
                return value;
            else
                return VLKeys.None;
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
            internal static readonly Dictionary<VLKeys, StrideKeys> MapKeys = NewMapKeys();

            /// <summary>
            /// Map between Stride keys and Winforms keys.
            /// </summary>
            internal static readonly Dictionary<StrideKeys, VLKeys> ReverseMapKeys = NewMapKeys().ToDictionary(e => e.Value, e => e.Key);


            private static Dictionary<VLKeys, StrideKeys> NewMapKeys()
            {
                var map = new Dictionary<VLKeys, StrideKeys>(200);
                map[VLKeys.None] = StrideKeys.None;
                map[VLKeys.Cancel] = StrideKeys.Cancel;
                map[VLKeys.Back] = StrideKeys.Back;
                map[VLKeys.Tab] = StrideKeys.Tab;
                map[VLKeys.LineFeed] = StrideKeys.LineFeed;
                map[VLKeys.Clear] = StrideKeys.Clear;
                map[VLKeys.Enter] = StrideKeys.Enter;
                map[VLKeys.Return] = StrideKeys.Return;
                map[VLKeys.Pause] = StrideKeys.Pause;
                map[VLKeys.Capital] = StrideKeys.Capital;
                map[VLKeys.CapsLock] = StrideKeys.CapsLock;
                map[VLKeys.HangulMode] = StrideKeys.HangulMode;
                map[VLKeys.KanaMode] = StrideKeys.KanaMode;
                map[VLKeys.JunjaMode] = StrideKeys.JunjaMode;
                map[VLKeys.FinalMode] = StrideKeys.FinalMode;
                map[VLKeys.HanjaMode] = StrideKeys.HanjaMode;
                map[VLKeys.KanjiMode] = StrideKeys.KanjiMode;
                map[VLKeys.Escape] = StrideKeys.Escape;
                map[VLKeys.IMEConvert] = StrideKeys.ImeConvert;
                map[VLKeys.IMENonconvert] = StrideKeys.ImeNonConvert;
                map[VLKeys.IMEAccept] = StrideKeys.ImeAccept;
                map[VLKeys.IMEModeChange] = StrideKeys.ImeModeChange;
                map[VLKeys.Space] = StrideKeys.Space;
                map[VLKeys.PageUp] = StrideKeys.PageUp;
                map[VLKeys.Prior] = StrideKeys.Prior;
                map[VLKeys.Next] = StrideKeys.Next;
                map[VLKeys.PageDown] = StrideKeys.PageDown;
                map[VLKeys.End] = StrideKeys.End;
                map[VLKeys.Home] = StrideKeys.Home;
                map[VLKeys.Left] = StrideKeys.Left;
                map[VLKeys.Up] = StrideKeys.Up;
                map[VLKeys.Right] = StrideKeys.Right;
                map[VLKeys.Down] = StrideKeys.Down;
                map[VLKeys.Select] = StrideKeys.Select;
                map[VLKeys.Print] = StrideKeys.Print;
                map[VLKeys.Execute] = StrideKeys.Execute;
                map[VLKeys.PrintScreen] = StrideKeys.PrintScreen;
                map[VLKeys.Snapshot] = StrideKeys.Snapshot;
                map[VLKeys.Insert] = StrideKeys.Insert;
                map[VLKeys.Delete] = StrideKeys.Delete;
                map[VLKeys.Help] = StrideKeys.Help;
                map[VLKeys.D0] = StrideKeys.D0;
                map[VLKeys.D1] = StrideKeys.D1;
                map[VLKeys.D2] = StrideKeys.D2;
                map[VLKeys.D3] = StrideKeys.D3;
                map[VLKeys.D4] = StrideKeys.D4;
                map[VLKeys.D5] = StrideKeys.D5;
                map[VLKeys.D6] = StrideKeys.D6;
                map[VLKeys.D7] = StrideKeys.D7;
                map[VLKeys.D8] = StrideKeys.D8;
                map[VLKeys.D9] = StrideKeys.D9;
                map[VLKeys.A] = StrideKeys.A;
                map[VLKeys.B] = StrideKeys.B;
                map[VLKeys.C] = StrideKeys.C;
                map[VLKeys.D] = StrideKeys.D;
                map[VLKeys.E] = StrideKeys.E;
                map[VLKeys.F] = StrideKeys.F;
                map[VLKeys.G] = StrideKeys.G;
                map[VLKeys.H] = StrideKeys.H;
                map[VLKeys.I] = StrideKeys.I;
                map[VLKeys.J] = StrideKeys.J;
                map[VLKeys.K] = StrideKeys.K;
                map[VLKeys.L] = StrideKeys.L;
                map[VLKeys.M] = StrideKeys.M;
                map[VLKeys.N] = StrideKeys.N;
                map[VLKeys.O] = StrideKeys.O;
                map[VLKeys.P] = StrideKeys.P;
                map[VLKeys.Q] = StrideKeys.Q;
                map[VLKeys.R] = StrideKeys.R;
                map[VLKeys.S] = StrideKeys.S;
                map[VLKeys.T] = StrideKeys.T;
                map[VLKeys.U] = StrideKeys.U;
                map[VLKeys.V] = StrideKeys.V;
                map[VLKeys.W] = StrideKeys.W;
                map[VLKeys.X] = StrideKeys.X;
                map[VLKeys.Y] = StrideKeys.Y;
                map[VLKeys.Z] = StrideKeys.Z;
                map[VLKeys.LWin] = StrideKeys.LeftWin;
                map[VLKeys.RWin] = StrideKeys.RightWin;
                map[VLKeys.Apps] = StrideKeys.Apps;
                map[VLKeys.Sleep] = StrideKeys.Sleep;
                map[VLKeys.NumPad0] = StrideKeys.NumPad0;
                map[VLKeys.NumPad1] = StrideKeys.NumPad1;
                map[VLKeys.NumPad2] = StrideKeys.NumPad2;
                map[VLKeys.NumPad3] = StrideKeys.NumPad3;
                map[VLKeys.NumPad4] = StrideKeys.NumPad4;
                map[VLKeys.NumPad5] = StrideKeys.NumPad5;
                map[VLKeys.NumPad6] = StrideKeys.NumPad6;
                map[VLKeys.NumPad7] = StrideKeys.NumPad7;
                map[VLKeys.NumPad8] = StrideKeys.NumPad8;
                map[VLKeys.NumPad9] = StrideKeys.NumPad9;
                map[VLKeys.Multiply] = StrideKeys.Multiply;
                map[VLKeys.Add] = StrideKeys.Add;
                map[VLKeys.Separator] = StrideKeys.Separator;
                map[VLKeys.Subtract] = StrideKeys.Subtract;
                map[VLKeys.Decimal] = StrideKeys.Decimal;
                map[VLKeys.Divide] = StrideKeys.Divide;
                map[VLKeys.F1] = StrideKeys.F1;
                map[VLKeys.F2] = StrideKeys.F2;
                map[VLKeys.F3] = StrideKeys.F3;
                map[VLKeys.F4] = StrideKeys.F4;
                map[VLKeys.F5] = StrideKeys.F5;
                map[VLKeys.F6] = StrideKeys.F6;
                map[VLKeys.F7] = StrideKeys.F7;
                map[VLKeys.F8] = StrideKeys.F8;
                map[VLKeys.F9] = StrideKeys.F9;
                map[VLKeys.F10] = StrideKeys.F10;
                map[VLKeys.F11] = StrideKeys.F11;
                map[VLKeys.F12] = StrideKeys.F12;
                map[VLKeys.F13] = StrideKeys.F13;
                map[VLKeys.F14] = StrideKeys.F14;
                map[VLKeys.F15] = StrideKeys.F15;
                map[VLKeys.F16] = StrideKeys.F16;
                map[VLKeys.F17] = StrideKeys.F17;
                map[VLKeys.F18] = StrideKeys.F18;
                map[VLKeys.F19] = StrideKeys.F19;
                map[VLKeys.F20] = StrideKeys.F20;
                map[VLKeys.F21] = StrideKeys.F21;
                map[VLKeys.F22] = StrideKeys.F22;
                map[VLKeys.F23] = StrideKeys.F23;
                map[VLKeys.F24] = StrideKeys.F24;
                map[VLKeys.NumLock] = StrideKeys.NumLock;
                map[VLKeys.Scroll] = StrideKeys.Scroll;
                map[VLKeys.LShiftKey] = StrideKeys.LeftShift;
                map[VLKeys.RShiftKey] = StrideKeys.RightShift;
                map[VLKeys.LControlKey] = StrideKeys.LeftCtrl;
                map[VLKeys.RControlKey] = StrideKeys.RightCtrl;
                map[VLKeys.LMenu] = StrideKeys.LeftAlt;
                map[VLKeys.RMenu] = StrideKeys.RightAlt;
                map[VLKeys.BrowserBack] = StrideKeys.BrowserBack;
                map[VLKeys.BrowserForward] = StrideKeys.BrowserForward;
                map[VLKeys.BrowserRefresh] = StrideKeys.BrowserRefresh;
                map[VLKeys.BrowserStop] = StrideKeys.BrowserStop;
                map[VLKeys.BrowserSearch] = StrideKeys.BrowserSearch;
                map[VLKeys.BrowserFavorites] = StrideKeys.BrowserFavorites;
                map[VLKeys.BrowserHome] = StrideKeys.BrowserHome;
                map[VLKeys.VolumeMute] = StrideKeys.VolumeMute;
                map[VLKeys.VolumeDown] = StrideKeys.VolumeDown;
                map[VLKeys.VolumeUp] = StrideKeys.VolumeUp;
                map[VLKeys.MediaNextTrack] = StrideKeys.MediaNextTrack;
                map[VLKeys.MediaPreviousTrack] = StrideKeys.MediaPreviousTrack;
                map[VLKeys.MediaStop] = StrideKeys.MediaStop;
                map[VLKeys.MediaPlayPause] = StrideKeys.MediaPlayPause;
                map[VLKeys.LaunchMail] = StrideKeys.LaunchMail;
                map[VLKeys.SelectMedia] = StrideKeys.SelectMedia;
                map[VLKeys.LaunchApplication1] = StrideKeys.LaunchApplication1;
                map[VLKeys.LaunchApplication2] = StrideKeys.LaunchApplication2;
                map[VLKeys.Oem1] = StrideKeys.Oem1;
                map[VLKeys.OemSemicolon] = StrideKeys.OemSemicolon;
                map[VLKeys.Oemplus] = StrideKeys.OemPlus;
                map[VLKeys.Oemcomma] = StrideKeys.OemComma;
                map[VLKeys.OemMinus] = StrideKeys.OemMinus;
                map[VLKeys.OemPeriod] = StrideKeys.OemPeriod;
                map[VLKeys.Oem2] = StrideKeys.Oem2;
                map[VLKeys.OemQuestion] = StrideKeys.OemQuestion;
                map[VLKeys.Oem3] = StrideKeys.Oem3;
                map[VLKeys.Oemtilde] = StrideKeys.OemTilde;
                map[VLKeys.Oem4] = StrideKeys.Oem4;
                map[VLKeys.OemOpenBrackets] = StrideKeys.OemOpenBrackets;
                map[VLKeys.Oem5] = StrideKeys.Oem5;
                map[VLKeys.OemPipe] = StrideKeys.OemPipe;
                map[VLKeys.Oem6] = StrideKeys.Oem6;
                map[VLKeys.OemCloseBrackets] = StrideKeys.OemCloseBrackets;
                map[VLKeys.Oem7] = StrideKeys.Oem7;
                map[VLKeys.OemQuotes] = StrideKeys.OemQuotes;
                map[VLKeys.Oem8] = StrideKeys.Oem8;
                map[VLKeys.Oem102] = StrideKeys.Oem102;
                map[VLKeys.OemBackslash] = StrideKeys.OemBackslash;
                map[VLKeys.Attn] = StrideKeys.Attn;
                map[VLKeys.Crsel] = StrideKeys.CrSel;
                map[VLKeys.Exsel] = StrideKeys.ExSel;
                map[VLKeys.EraseEof] = StrideKeys.EraseEof;
                map[VLKeys.Play] = StrideKeys.Play;
                map[VLKeys.Zoom] = StrideKeys.Zoom;
                map[VLKeys.NoName] = StrideKeys.NoName;
                map[VLKeys.Pa1] = StrideKeys.Pa1;
                map[VLKeys.OemClear] = StrideKeys.OemClear;
                return map;
            }
        }
    }
}
