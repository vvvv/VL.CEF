using Stride.Core.Mathematics;
using System;
using System.Windows.Forms;
using VL.Lib.IO.Notifications;
using VL.Skia;
using Xilium.CefGlue;

namespace VL.CEF
{
    partial class SkiaRenderHandler
    {
        public bool Notify(INotification notification, CallerInfo caller)
        {
            if (notification is MouseNotification mouseNotification)
            {
                HandleMouseNotification(mouseNotification, caller);
                return true;
            }
            else if (notification is KeyNotification keyNotification)
            {
                HandleKeyNotification(keyNotification, caller);
                return true;
            }
            else if (notification is TouchNotification touchNotification)
            {
                HandleTouchNotification(touchNotification, caller);
                return true;
            }
            return false;
        }

        private void HandleTouchNotification(TouchNotification n, CallerInfo caller)
        {
            var position = GetPositionInViewport(n, caller);
            var touchEvent = new CefTouchEvent()
            {
                Id = n.Id,
                //Modifiers = (CefEventFlags)((int)(FKeyboard.Modifiers) >> 15),
                PointerType = CefPointerType.Touch,
                Pressure = 1f,
                RadiusX = n.ContactArea.X,
                RadiusY = n.ContactArea.Y,
                RotationAngle = 0,
                Type = GetTouchType(n.Kind),
                X = position.X,
                Y = position.Y
            };
            webRenderer.BrowserHost.SendTouchEvent(touchEvent);

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

        private void HandleKeyNotification(KeyNotification n, CallerInfo caller)
        {
            var keyEvent = new CefKeyEvent()
            {
                //Modifiers = (CefEventFlags)((int)(FKeyboard.Modifiers) >> 15)
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
            webRenderer.BrowserHost.SendKeyEvent(keyEvent);
        }

        private void HandleMouseNotification(MouseNotification n, CallerInfo caller)
        {
            var position = GetPositionInViewport(n, caller);
            var mouseEvent = new CefMouseEvent((int)position.X, (int)position.Y, CefEventFlags.None);
            var browserHost = webRenderer.BrowserHost;
            switch (n.Kind)
            {
                case MouseNotificationKind.MouseDown:
                    var mouseDown = n as MouseDownNotification;
                    browserHost.SendMouseClickEvent(mouseEvent, GetMouseButtonType(mouseDown.Buttons), mouseUp: false, clickCount: 1);
                    break;
                case MouseNotificationKind.MouseUp:
                    var mouseUp = n as MouseUpNotification;
                    browserHost.SendMouseClickEvent(mouseEvent, GetMouseButtonType(mouseUp.Buttons), mouseUp: true, clickCount: 1);
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
                case MouseNotificationKind.MouseClick:
                    var mouseClick = n as MouseClickNotification;
                    browserHost.SendMouseClickEvent(mouseEvent, GetMouseButtonType(mouseClick.Buttons), mouseUp: false, clickCount: mouseClick.ClickCount);
                    break;
                case MouseNotificationKind.DeviceLost:
                    browserHost.SendMouseMoveEvent(mouseEvent, mouseLeave: true);
                    break;
                default:
                    throw new NotImplementedException();
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
        }

        private Vector2 GetPositionInViewport(INotificationWithSpacePositions n, CallerInfo callerInfo)
        {
            var position = n.PositionInWorldSpace;
            var p = callerInfo.Transformation.MapPoint(position.X, position.Y) - callerInfo.ViewportBounds.Location;
            return new Vector2(p.X, p.Y).DeviceToLogical(webRenderer.ScaleFactor);
        }
    }
}
