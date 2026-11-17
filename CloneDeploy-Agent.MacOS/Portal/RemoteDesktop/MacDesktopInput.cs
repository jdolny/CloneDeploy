// Adapted from ControlR MacInterop and MacInputSimulation (MIT).
// Copyright (c) 2026 Jared Goodwin. See ControlR-LICENSE.txt.
using System.Text.Json;
using static CloneDeploy_Agent.MacOS.Portal.MacDesktopNative;

namespace CloneDeploy_Agent.MacOS.Portal;

internal sealed class MacDesktopInput : IDisposable
{
    private const uint LeftMouseDown = 1, LeftMouseUp = 2, RightMouseDown = 3, RightMouseUp = 4;
    private const uint MouseMoved = 5, LeftMouseDragged = 6, RightMouseDragged = 7;
    private const uint OtherMouseDown = 25, OtherMouseUp = 26, OtherMouseDragged = 27;
    private const uint MouseClickStateField = 1;
    private const int MaximumHeldKeys = 128;
    private const int MaximumKeyCodeCharacters = 40;
    private const int MaximumKeyTextCharacters = 16;
    private const int DoubleClickMilliseconds = 500;
    private const int DoubleClickDistance = 5;
    private const int MaximumClickCount = 3;

    private const ulong ShiftFlag = 0x20000UL;
    private const ulong ControlFlag = 0x40000UL;
    private const ulong AlternateFlag = 0x80000UL;
    private const ulong CommandFlag = 0x100000UL;
    private const ulong ShortcutModifierFlags = 0x1C0000;
    private const int MaximumScrollDelta = 1200;

    private readonly Dictionary<string, (ushort Key, string? Text)> keys = [];
    private readonly HashSet<int> buttons = [];
    private Point pointer;
    private DateTime lastClick;
    private Point lastPoint;
    private int lastButton = -1, clicks;
    internal MacDesktopNative.Monitor? Display { get; set; }
    private ulong Flags => (keys.ContainsKey("ShiftLeft") || keys.ContainsKey("ShiftRight") ? ShiftFlag : 0) |
        (keys.ContainsKey("ControlLeft") || keys.ContainsKey("ControlRight") ? ControlFlag : 0) |
        (keys.ContainsKey("AltLeft") || keys.ContainsKey("AltRight") ? AlternateFlag : 0) |
        (keys.ContainsKey("MetaLeft") || keys.ContainsKey("MetaRight") ? CommandFlag : 0);

    internal void Apply(JsonElement input)
    {
        var kind = input.GetProperty("type").GetString();
        if (kind == "release") { Dispose(); return; }
        if (!ConsoleAvailable() || !CGPreflightPostEventAccess()) { keys.Clear(); buttons.Clear(); return; }
        if (kind == "key")
        {
            var code = input.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var text = input.TryGetProperty("keyText", out var t) ? t.GetString() ?? "" : "";
            if (code.Length > MaximumKeyCodeCharacters || text.Length > MaximumKeyTextCharacters) return;
            var down = input.GetProperty("down").GetBoolean();
            if (!down)
            {
                if (keys.Remove(code, out var held)) Key(held.Key, held.Text, false);
                return;
            }
            if (keys.Count >= MaximumHeldKeys) return;
            var printable = text.Length is > 0 and <= 2 && !text.Any(char.IsControl) && (Flags & ShortcutModifierFlags) == 0;
            if (!Mapping.TryGetValue(code, out var key) && !printable) return;
            keys[code] = (key, printable ? text : null);
            Key(key, printable ? text : null, true);
            return;
        }
        if (kind is not ("move" or "button" or "wheel") || Display is not { } display) return;
        if (!input.TryGetProperty("monitor", out var id) || id.GetString() != display.Id) return;
        var x = input.GetProperty("x").GetDouble(); var y = input.GetProperty("y").GetDouble();
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) return;
        pointer = new(display.Left + x * (display.Width - 1), display.Top + y * (display.Height - 1));
        if (kind == "move")
        {
            var button = buttons.Contains(0) ? 0 : buttons.Contains(2) ? 2 : buttons.Contains(1) ? 1 : -1;
            Post(CGEventCreateMouseEvent(0, button == 0 ? LeftMouseDragged : button == 2 ? RightMouseDragged : button == 1 ? OtherMouseDragged : MouseMoved, pointer, NativeButton(button)));
        }
        else if (kind == "button")
        {
            var button = input.GetProperty("button").GetInt32(); if (button is < 0 or > 2) return;
            var down = input.GetProperty("down").GetBoolean();
            if (down)
            {
                clicks = lastButton == button && DateTime.UtcNow - lastClick < TimeSpan.FromMilliseconds(DoubleClickMilliseconds) &&
                    Math.Abs(lastPoint.X - pointer.X) < DoubleClickDistance && Math.Abs(lastPoint.Y - pointer.Y) < DoubleClickDistance ? Math.Min(clicks + 1, MaximumClickCount) : 1;
                lastClick = DateTime.UtcNow; lastPoint = pointer; lastButton = button; buttons.Add(button);
            }
            else buttons.Remove(button);
            Button(button, down);
        }
        else
        {
            var delta = Math.Clamp(input.GetProperty("delta").GetInt32(), -MaximumScrollDelta, MaximumScrollDelta);
            Post(CGEventCreateMouseEvent(0, MouseMoved, pointer, 0));
            Post(Scroll(0, 0, 1, delta));
        }
    }
    private static uint NativeButton(int button) => button switch { 2 => 1, 1 => 2, _ => 0 };
    private void Button(int button, bool down)
    {
        var value = CGEventCreateMouseEvent(0, button switch { 0 => down ? LeftMouseDown : LeftMouseUp, 2 => down ? RightMouseDown : RightMouseUp, _ => down ? OtherMouseDown : OtherMouseUp }, pointer, NativeButton(button));
        if (value != 0) CGEventSetIntegerValueField(value, MouseClickStateField, clicks);
        Post(value);
    }
    private void Key(ushort key, string? text, bool down)
    {
        var value = CGEventCreateKeyboardEvent(0, key, down);
        if (value != 0 && text is not null) CGEventKeyboardSetUnicodeString(value, (nuint)text.Length, text.ToCharArray());
        Post(value);
    }
    private void Post(nint value)
    {
        if (value == 0) throw new IOException(PortalText.Get("RemoteSupport.InputEventFailed"));
        try { CGEventSetFlags(value, Flags); CGEventPost(0, value); }
        finally { CFRelease(value); }
    }
    public void Dispose()
    {
        try
        {
            if (ConsoleAvailable() && CGPreflightPostEventAccess())
            {
                foreach (var pair in keys.ToArray())
                { keys.Remove(pair.Key); try { Key(pair.Value.Key, pair.Value.Text, false); } catch (IOException) { } }
                foreach (var button in buttons) { try { Button(button, false); } catch (IOException) { } }
            }
        }
        finally { keys.Clear(); buttons.Clear(); }
    }
    private static readonly Dictionary<string, ushort> Mapping = new()
    {
        ["KeyA"]=0,["KeyS"]=1,["KeyD"]=2,["KeyF"]=3,["KeyH"]=4,["KeyG"]=5,["KeyZ"]=6,["KeyX"]=7,["KeyC"]=8,["KeyV"]=9,["KeyB"]=11,
        ["KeyQ"]=12,["KeyW"]=13,["KeyE"]=14,["KeyR"]=15,["KeyY"]=16,["KeyT"]=17,["Digit1"]=18,["Digit2"]=19,["Digit3"]=20,
        ["Digit4"]=21,["Digit6"]=22,["Digit5"]=23,["Equal"]=24,["Digit9"]=25,["Digit7"]=26,["Minus"]=27,["Digit8"]=28,["Digit0"]=29,
        ["BracketRight"]=30,["KeyO"]=31,["KeyU"]=32,["BracketLeft"]=33,["KeyI"]=34,["KeyP"]=35,["Enter"]=36,["KeyL"]=37,
        ["KeyJ"]=38,["Quote"]=39,["KeyK"]=40,["Semicolon"]=41,["Backslash"]=42,["Comma"]=43,["Slash"]=44,["KeyN"]=45,
        ["KeyM"]=46,["Period"]=47,["Tab"]=48,["Space"]=49,["Backquote"]=50,["Backspace"]=51,["Escape"]=53,
        ["MetaRight"]=54,["MetaLeft"]=55,["ShiftLeft"]=56,["CapsLock"]=57,["AltLeft"]=58,["ControlLeft"]=59,
        ["ShiftRight"]=60,["AltRight"]=61,["ControlRight"]=62,["NumpadDecimal"]=65,["NumpadMultiply"]=67,["NumpadAdd"]=69,
        ["NumLock"]=71,["NumpadDivide"]=75,["NumpadEnter"]=76,["NumpadSubtract"]=78,["NumpadEqual"]=81,
        ["Numpad0"]=82,["Numpad1"]=83,["Numpad2"]=84,["Numpad3"]=85,["Numpad4"]=86,["Numpad5"]=87,["Numpad6"]=88,
        ["Numpad7"]=89,["Numpad8"]=91,["Numpad9"]=92,["F5"]=96,["F6"]=97,["F7"]=98,["F3"]=99,["F8"]=100,["F9"]=101,
        ["F11"]=103,["F13"]=105,["F16"]=106,["F14"]=107,["F10"]=109,["F12"]=111,["F15"]=113,["Insert"]=114,
        ["Home"]=115,["PageUp"]=116,["Delete"]=117,["F4"]=118,["End"]=119,["F2"]=120,["PageDown"]=121,["F1"]=122,
        ["ArrowLeft"]=123,["ArrowRight"]=124,["ArrowDown"]=125,["ArrowUp"]=126
    };
}
