// Ported from ToemsProxyDHCP. See THIRD-PARTY-NOTICES.txt.
#nullable disable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace CloneDeploy_Server.Backend.Services
{
   
    /// <summary>
    /// DHCP request
    /// </summary>
    public class DHCPRequest
    {
        public enum ClientSystemArch
        {
            Intelx86PC = 0,
            EFIIA32 = 6,
            UefiX64 = 7,
            EfiByteCode = 9,
            EfiArm64 = 11,
            Error = 99
        }

        public IPAddress ServerIdentifier { get; }
        internal PxePacketConfiguration Configuration { get; }
        internal PxeSettingsDto Settings { get; }
        public readonly DHCPMessage.Packet requestData;
        
        private readonly IPEndPoint remote;
        private const int OPTION_OFFSET = 240;
       

        internal DHCPRequest(byte[] data, IPEndPoint source, PxeSettingsDto settings)
            : this(data, source, new PxePacketConfiguration(settings)) { }

        internal DHCPRequest(byte[] data, IPEndPoint source, PxePacketConfiguration configuration)
        {
            Configuration = configuration;
            Settings = configuration.Settings;
            ServerIdentifier = configuration.ServerIdentifier;
            
            using var stm = new System.IO.MemoryStream(data, 0, data.Length);
            using var rdr = new System.IO.BinaryReader(stm);
            // Reading data
            requestData.op = rdr.ReadByte();
            requestData.htype = rdr.ReadByte();
            requestData.hlen = rdr.ReadByte();
            requestData.hops = rdr.ReadByte();
            requestData.xid = rdr.ReadBytes(4);
            requestData.secs = rdr.ReadBytes(2);
            requestData.flags = rdr.ReadBytes(2);
            requestData.ciaddr = rdr.ReadBytes(4);
            requestData.yiaddr = rdr.ReadBytes(4);
            requestData.siaddr = rdr.ReadBytes(4);
            requestData.giaddr = rdr.ReadBytes(4);
            requestData.chaddr = rdr.ReadBytes(16);
            requestData.sname = rdr.ReadBytes(64);
            requestData.file = rdr.ReadBytes(128);
            requestData.mcookie = rdr.ReadBytes(4);
            requestData.options = rdr.ReadBytes(data.Length - OPTION_OFFSET);
            
            remote = source;

        }

        public int GetSourcePort()
        {
            return remote.Port;
        }

        /// <summary>
        /// Returns type of DHCP request
        /// </summary>
        /// <returns>DHCP message type</returns>
        public DHCPMsgType GetMsgType()
        {
            byte[] DData;
            DData = OptionData.GetOptionData(DHCPOption.DHCPMessageTYPE,requestData);
            if (DData != null && DData.Length > 0)
                return (DHCPMsgType)DData[0];
            return 0;
        }

        public ClientSystemArch GetClientSystemArch()
        {
            var data = OptionData.GetOptionData(DHCPOption.ClientSystemArchitecture, requestData);
            if (data == null || data.Length == 0 || data.Length % 2 != 0)
                return ClientSystemArch.Error;

            // Option 93 lists 16-bit architecture IDs in client preference order.
            for (var offset = 0; offset < data.Length; offset += 2)
            {
                var architecture = (ClientSystemArch)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                if (architecture is ClientSystemArch.Intelx86PC or ClientSystemArch.EFIIA32
                    or ClientSystemArch.UefiX64 or ClientSystemArch.EfiByteCode or ClientSystemArch.EfiArm64)
                    return architecture;
            }
            return ClientSystemArch.Error;
        }

        public byte[] GetVendorOptionData()
        {
            return OptionData.GetOptionData(DHCPOption.Vendorclassidentifier,requestData);
        }
    }
}
