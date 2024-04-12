using Stride.Core.Mathematics;
using System;
using System.Diagnostics;
using VL.Core;
using VL.Lib.IO;
using VL.Lib.IO.Notifications;
using Xilium.CefGlue;

namespace VL.CEF
{
    partial class WebBrowser
    {
        CefEventFlags mouseModifiers;

        public bool SendNotification(INotification notification, Func<NotificationWithPosition, Vector2> getPosition)
        {
            if (notification is MouseNotification mouseNotification)
            {
                HandleMouseNotification(mouseNotification, getPosition);
                return true;
            }
            else if (notification is KeyNotification keyNotification)
            {
                HandleKeyNotification(keyNotification);
                return true;
            }
            else if (notification is TouchNotification touchNotification)
            {
                HandleTouchNotification(touchNotification, getPosition);
                return true;
            }
            return false;
        }

        private void HandleTouchNotification(TouchNotification n, Func<NotificationWithPosition, Vector2> getPosition)
        {
            var position = GetPositionInBrowserSpace(n, getPosition);
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
                X = position.X,
                Y = position.Y
            };
            BrowserHost.SendTouchEvent(touchEvent);

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

        private void HandleKeyNotification(KeyNotification n)
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
        }

        int clickCount = 1;
        MouseButtons? lastButton;
        Vector2 lastPosition;
        Stopwatch stopwatch = Stopwatch.StartNew();

        private void HandleMouseNotification(MouseNotification n, Func<NotificationWithPosition, Vector2> getPosition)
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
                var position = GetPositionInBrowserSpace(n, getPosition);
                var mouseEvent = new CefMouseEvent((int)position.X, (int)position.Y, GetModifiers(n));
                var browserHost = BrowserHost;
                switch (n.Kind)
                {
                    case MouseNotificationKind.MouseDown:
                        var mouseDown = n as MouseDownNotification;
                        browserHost.SendMouseClickEvent(mouseEvent, GetMouseButtonType(mouseDown.Buttons), mouseUp: false, clickCount: clickCount);
                        break;
                    case MouseNotificationKind.MouseUp:
                        var mouseUp = n as MouseUpNotification;
                        browserHost.SendMouseClickEvent(mouseEvent, GetMouseButtonType(mouseUp.Buttons), mouseUp: true, clickCount: clickCount);
                        break;
                    case MouseNotificationKind.MouseMove:
                        browserHost.SendMouseMoveEvent(mouseEvent, mouseLeave: false);
                        break;
                    case MouseNotificationKind.MouseWheel:
                        var mouseWheel = n as MouseWheelNotification;
                        browserHost.SendMouseWheelEvent(mouseEvent, 0, mouseWheel.WheelDelta);
                        break;
                    case MouseNotificationKind.MouseHorizontalWheel:
                        var mouseHWheel = n as MouseHorizontalWheelNotification;
                        browserHost.SendMouseWheelEvent(mouseEvent, mouseHWheel.WheelDelta, 0);
                        break;
                    case MouseNotificationKind.DeviceLost:
                        browserHost.SendMouseMoveEvent(mouseEvent, mouseLeave: true);
                        break;
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

        Vector2 GetPositionInBrowserSpace(NotificationWithPosition notification, Func<NotificationWithPosition, Vector2> getPosition)
        {
            return getPosition(notification).DeviceToLogical(ScaleFactor);
        }
    }
}
