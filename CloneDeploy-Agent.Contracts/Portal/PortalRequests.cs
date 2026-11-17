namespace CloneDeploy_Agent.Contracts;

/// <summary>Request bytes used by the Windows portal pipe. Values are part of the wire format.</summary>
public static class PortalRequests
{
    public const byte Snapshot = 1;
    public const byte Notifications = 2;
    public const byte PolicyIcon = 3;
    public const byte AdminSignIn = 4;
    public const byte AdminStatus = 5;
    public const byte AdminSignOut = 6;
    public const byte StartDriverPack = 7;
    public const byte DriverPackStatus = 8;
    public const byte CancelDriverPack = 9;
    public const byte Usb = 10;
    public const byte AcknowledgeNotification = 11;
    public const byte NotificationSnapshot = 12;
    public const byte ImagePrep = 13;
    public const byte RemoteConsent = 14;
    public const byte Help = 15;
    public const byte HelpPresence = 16;
    public const byte WorkAccount = 17;
}
