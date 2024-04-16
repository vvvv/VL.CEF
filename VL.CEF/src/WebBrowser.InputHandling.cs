using Stride.Core.Mathematics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VL.Core;
using VL.Lib.IO;
using VL.Lib.IO.Notifications;
using Xilium.CefGlue;

namespace VL.CEF
{
    partial class WebBrowser
    {
        private readonly HashSet<MouseButtons> downButtons = new();
        private readonly HashSet<(long deviceId, int touchId)> downTouches = new();

        CefEventFlags mouseModifiers;

        public bool SendNotification(INotification notification, Func<NotificationWithPosition, Vector2> getPosition)
        {
            if (notification is MouseNotification mouseNotification)
                return HandleMouseNotification(mouseNotification, getPosition);
            if (notification is KeyNotification keyNotification)
                return HandleKeyNotification(keyNotification);
            if (notification is TouchNotification touchNotification)
                return HandleTouchNotification(touchNotification, getPosition);
            return false;
        }

        private bool HandleTouchNotification(TouchNotification n, Func<NotificationWithPosition, Vector2> getPosition)
        {
            var position = getPosition(n);
            if (n.Kind == TouchNotificationKind.TouchDown)
            {
                if (!Bounds.Contains(position))
                    return false;

                downTouches.Add((n.TouchDeviceID, n.Id));
            }
            else if (n.Kind == TouchNotificationKind.TouchUp)
            {
                if (!downTouches.Remove((n.TouchDeviceID, n.Id)))
                    return false;
            }
            else if (n.Kind == TouchNotificationKind.TouchMove)
            {
                if (!downTouches.Contains((n.TouchDeviceID, n.Id)))
                    return false;
            }

            var browserPosition = position.DeviceToLogical(ScaleFactor);
            var touchEvent = new CefTouchEvent()
            {
                Id = n.Id,
                Modifiers = GetModifiers(n),
                PointerType = CefPointerType.Touch,
                Pressure = 1f,
                RadiusX = n.ContactArea.X,
                RadiusY = n.ContactArea.Y,
                RotationAngle = 0,
                Type = GetTouchType(n.Kind),
                X = browserPosition.X,
                Y = browserPosition.Y
            };
            BrowserHost.SendTouchEvent(touchEvent);
            return true;

            CefTouchEventType GetTouchType(TouchNotificationKind kind)
            {
                switch (kind)
                {
                    case TouchNotificationKind.TouchDown:
                        return CefTouchEventType.Pressed;
                    case TouchNotificationKind.TouchUp:
                        return CefTouchEventType.Released;
                    case TouchNotificationKind.TouchMove:
                        return CefTouchEventType.Moved;
                    default:
                        return CefTouchEventType.Cancelled;
                }
            }
        }

        private bool HandleKeyNotification(KeyNotification n)
        {
            var keyEvent = new CefKeyEvent()
            {
                Modifiers = GetModifiers(n)
            };
            switch (n.Kind)
            {
                case KeyNotificationKind.KeyDown:
                    var keyDown = n as KeyDownNotification;
                    keyEvent.EventType = CefKeyEventType.KeyDown;
                    keyEvent.WindowsKeyCode = (int)keyDown.KeyCode;
                    keyEvent.NativeKeyCode = (int)keyDown.KeyCode;
                    break;
                case KeyNotificationKind.KeyPress:
                    var keyPress = n as KeyPressNotification;
                    keyEvent.EventType = CefKeyEventType.Char;
                    keyEvent.Character = keyPress.KeyChar;
                    keyEvent.UnmodifiedCharacter = keyPress.KeyChar;
                    keyEvent.WindowsKeyCode = (int)keyPress.KeyChar;
                    keyEvent.NativeKeyCode = (int)keyPress.KeyChar;
                    break;
                case KeyNotificationKind.KeyUp:
                    var keyUp = n as KeyUpNotification;
                    keyEvent.EventType = CefKeyEventType.KeyUp;
                    keyEvent.WindowsKeyCode = (int)keyUp.KeyCode;
                    keyEvent.NativeKeyCode = (int)keyUp.KeyCode;
                    break;
                default:
                    break;
            }
            BrowserHost.SendKeyEvent(keyEvent);
            return true;
        }

        int clickCount = 1;
        MouseButtons? lastButton;
        Vector2 lastPosition;
        Stopwatch stopwatch = Stopwatch.StartNew();

        private bool HandleMouseNotification(MouseNotification n, Func<NotificationWithPosition, Vector2> getPosition)
        {
            if (n is MouseButtonNotification buttonNotification)
            {
                var position = n.Position;
                var delta = lastPosition - position;
                var deltaTime = stopwatch.ElapsedMilliseconds;
                stopwatch.Restart();

                if (n is MouseDownNotification)
                {
                    if (buttonNotification.Buttons == lastButton &&
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
                else if (buttonNotification.Buttons != lastButton)
                {
                    clickCount = 1;
                }

                lastButton = buttonNotification.Buttons;
                lastPosition = position;

                if (n is MouseDownNotification mouseDown)
                    mouseModifiers |= ToCefEventFlags(mouseDown.Buttons);
                else if (n is MouseUpNotification mouseUp)
                    mouseModifiers &= ~ToCefEventFlags(mouseUp.Buttons);
            }

            {
                var position = getPosition(n);
                var browserPosition = position.DeviceToLogical(ScaleFactor);
                var mouseEvent = new CefMouseEvent((int)browserPosition.X, (int)browserPosition.Y, GetModifiers(n));
                var browserHost = BrowserHost;
                switch (n.Kind)
                {
                    case MouseNotificationKind.MouseDown:
                        var mouseDown = n as MouseDownNotification;
                        if (Bounds.Contains(position))
                        {
                            downButtons.Add(mouseDown.Buttons);
                            browserHost.SendMouseClickEvent(mouseEvent, GetMouseButtonType(mouseDown.Buttons), mouseUp: false, clickCount: clickCount);
                            return true;
                        }
                        return false;
                    case MouseNotificationKind.MouseUp:
                        var mouseUp = n as MouseUpNotification;
                        if (downButtons.Contains(mouseUp.Buttons))
                        {
                            downButtons.Remove(mouseUp.Buttons);
                            browserHost.SendMouseClickEvent(mouseEvent, GetMouseButtonType(mouseUp.Buttons), mouseUp: true, clickCount: clickCount);
                            return true;
                        }
                        return false;
                    case MouseNotificationKind.MouseMove:
                        browserHost.SendMouseMoveEvent(mouseEvent, mouseLeave: false);
                        return true;
                    case MouseNotificationKind.MouseWheel:
                        if (!Bounds.Contains(position))
                            return false;

                        var mouseWheel = n as MouseWheelNotification;
                        browserHost.SendMouseWheelEvent(mouseEvent, 0, mouseWheel.WheelDelta);
                        return true;
                    case MouseNotificationKind.MouseHorizontalWheel:
                        if (!Bounds.Contains(position))
                            return false;

                        var mouseHWheel = n as MouseHorizontalWheelNotification;
                        browserHost.SendMouseWheelEvent(mouseEvent, mouseHWheel.WheelDelta, 0);
                        return true;
                    case MouseNotificationKind.DeviceLost:
                        downButtons.Clear();
                        browserHost.SendMouseMoveEvent(mouseEvent, mouseLeave: true);
                        return true;
                    default:
                        return false;
                }
            }

            CefMouseButtonType GetMouseButtonType(MouseButtons buttons)
            {
                if ((buttons & MouseButtons.Left) != 0)
                    return CefMouseButtonType.Left;
                if ((buttons & MouseButtons.Middle) != 0)
                    return CefMouseButtonType.Middle;
                if ((buttons & MouseButtons.Right) != 0)
                    return CefMouseButtonType.Right;
                return default;
            }

            static CefEventFlags ToCefEventFlags(MouseButtons buttons)
            {
                switch (buttons)
                {
                    case MouseButtons.Left:
                        return CefEventFlags.LeftMouseButton;
                    case MouseButtons.Middle:
                        return CefEventFlags.MiddleMouseButton;
                    case MouseButtons.Right:
                        return CefEventFlags.RightMouseButton;
                }
                return default;
            }
        }

        CefEventFlags GetModifiers(NotificationBase n)
        {
            var result = CefEventFlags.None;
            if (n.AltKey)
                result |= CefEventFlags.AltDown | CefEventFlags.IsLeft;
            if (n.ShiftKey)
                result |= CefEventFlags.ShiftDown | CefEventFlags.IsLeft;
            if (n.CtrlKey)
                result |= CefEventFlags.ControlDown | CefEventFlags.IsLeft;
            return result | mouseModifiers;
        }

        private RectangleF Bounds => new RectangleF(0, 0, Size.X, Size.Y);
    }
}
