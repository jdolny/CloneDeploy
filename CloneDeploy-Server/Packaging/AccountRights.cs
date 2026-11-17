using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CloneDeploy.Setup
{
    // Compiled by Windows PowerShell during installation only.
    public static class AccountRights
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Attributes
        {
            public int Length;
            public IntPtr RootDirectory, ObjectName;
            public uint Flags;
            public IntPtr SecurityDescriptor, SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length, MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("advapi32.dll")]
        private static extern uint LsaOpenPolicy(IntPtr system, ref Attributes attributes, uint access, out IntPtr policy);
        [DllImport("advapi32.dll")]
        private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, UnicodeString[] rights, uint count);
        [DllImport("advapi32.dll")]
        private static extern uint LsaNtStatusToWinError(uint status);
        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr policy);

        public static void Grant(string sidText) { Grant(sidText, false); }
        public static void GrantServiceOnly(string sidText) { Grant(sidText, true); }
        private static void Grant(string sidText, bool serviceOnly)
        {
            var attributes = new Attributes { Length = Marshal.SizeOf(typeof(Attributes)) };
            IntPtr policy;
            uint result = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x810, out policy);
            if (result != 0) throw new Win32Exception((int)LsaNtStatusToWinError(result));
            try
            {
                var sid = new SecurityIdentifier(sidText);
                var bytes = new byte[sid.BinaryLength];
                sid.GetBinaryForm(bytes, 0);
                foreach (var right in (serviceOnly ? new[] { "SeServiceLogonRight" } : new[] { "SeServiceLogonRight", "SeCreateSymbolicLinkPrivilege" }))
                {
                    var buffer = Marshal.StringToHGlobalUni(right);
                    try
                    {
                        var value = new UnicodeString { Buffer = buffer, Length = (ushort)(right.Length * 2), MaximumLength = (ushort)((right.Length + 1) * 2) };
                        result = LsaAddAccountRights(policy, bytes, new[] { value }, 1);
                        if (result != 0) throw new Win32Exception((int)LsaNtStatusToWinError(result));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { LsaClose(policy); }
        }
    }
}
