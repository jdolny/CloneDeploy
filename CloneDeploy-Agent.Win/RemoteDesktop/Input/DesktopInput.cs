// Input event handling follows ControlR's Windows SendInput approach (MIT).
// Copyright (c) 2026 Jared Goodwin. See ControlR-LICENSE.txt.
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Applies bounded browser input to the selected monitor and releases held keys on disconnect.</summary>
internal sealed class DesktopInput : IDisposable
{
    private const uint MouseInput = 0, KeyboardInput = 1;
    private const uint AbsoluteVirtualDesktopMove = 0xC001;
    private const uint MouseWheel = 0x0800;
    private const uint KeyUp = 2, ExtendedKey = 1;
    private const uint LeftDown = 2, LeftUp = 4, MiddleDown = 32, MiddleUp = 64, RightDown = 8, RightUp = 16;
    private const int VirtualScreenX = 76, VirtualScreenY = 77, VirtualScreenWidth = 78, VirtualScreenHeight = 79;
    private const int MinimumVirtualKey = 8, MaximumVirtualKey = 254;
    private const int PageUpKey = 33, DeleteKey = 46, LeftWindowsKey = 91, RightWindowsKey = 92, DivideKey = 111;
    private const int MaximumScrollDelta = 1200;

    internal string? MonitorId { get; set; }
    private readonly HashSet<ushort> heldKeys = [];
    private readonly HashSet<int> heldButtons = [];
    internal void Apply(byte[] message)
    {
        using var desktop = new DesktopAttachment();
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var kind = root.GetProperty("type").GetString();
        if (kind == "release") { Dispose(); return; }
        if (kind == "key")
        {
            var key = root.GetProperty("key").GetInt32();
            if (key is < MinimumVirtualKey or > MaximumVirtualKey) return;
            var down = root.GetProperty("down").GetBoolean();
            Key((ushort)key, down);
            if (down) heldKeys.Add((ushort)key); else heldKeys.Remove((ushort)key);
            return;
        }
        if (kind is not ("move" or "button" or "wheel")) return;
        var x = root.GetProperty("x").GetDouble();
        var y = root.GetProperty("y").GetDouble();
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) return;
        var monitor = DesktopMonitors.Resolve(MonitorId);
        // Ignore a pointer event from a stale frame after the operator switches displays.
        if (root.TryGetProperty("monitor", out var requested) && requested.GetString() != monitor.Id) return;
        var point = DesktopMonitors.Map(monitor, x, y, DesktopNative.GetSystemMetrics(VirtualScreenX), DesktopNative.GetSystemMetrics(VirtualScreenY),
            DesktopNative.GetSystemMetrics(VirtualScreenWidth), DesktopNative.GetSystemMetrics(VirtualScreenHeight));
        Send(new() { Type = MouseInput, Data = new() { Mouse = new() { X = point.X, Y = point.Y, Flags = AbsoluteVirtualDesktopMove } } }); // ABSOLUTE | VIRTUALDESK | MOVE
        if (kind == "button")
        {
            var button = root.GetProperty("button").GetInt32();
            if (button is < 0 or > 2) return;
            var down = root.GetProperty("down").GetBoolean();
            Button(button, down);
            if (down) heldButtons.Add(button); else heldButtons.Remove(button);
        }
        if (kind == "wheel")
        {
            var delta = Math.Clamp(root.GetProperty("delta").GetInt32(), -MaximumScrollDelta, MaximumScrollDelta);
            Send(new() { Type = MouseInput, Data = new() { Mouse = new() { Flags = MouseWheel, Data = unchecked((uint)delta) } } });
        }
    }
    private static void Key(ushort key, bool down) => Send(new() { Type = KeyboardInput, Data = new() { Keyboard = new() { Key = key, Flags = (down ? 0u : KeyUp) | (key is >= PageUpKey and <= DeleteKey or LeftWindowsKey or RightWindowsKey or DivideKey ? ExtendedKey : 0u) } } });
    private static void Button(int button, bool down) => Send(new() { Type = MouseInput, Data = new() { Mouse = new() { Flags = button switch { 0 => down ? LeftDown : LeftUp, 1 => down ? MiddleDown : MiddleUp, _ => down ? RightDown : RightUp } } } });
    private static void Send(DesktopNative.Input input)
    {
        if (DesktopNative.SendInput(1, [input], Marshal.SizeOf<DesktopNative.Input>()) != 1)
            throw new DesktopUnavailableException("Windows rejected remote input while the desktop was unavailable.");
    }
    public void Dispose()
    {
        // Disconnect can run on another thread or after Windows has switched desktops.
        try
        {
            using var desktop = new DesktopAttachment();
            foreach (var key in heldKeys.ToArray()) { try { Key(key, false); } catch (IOException) { } }
            foreach (var button in heldButtons.ToArray()) { try { Button(button, false); } catch (IOException) { } }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
        finally { heldKeys.Clear(); heldButtons.Clear(); }
    }
}
