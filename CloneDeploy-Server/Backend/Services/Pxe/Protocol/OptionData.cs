// Ported from ToemsProxyDHCP. See THIRD-PARTY-NOTICES.txt.
#nullable disable
using System;

namespace CloneDeploy_Server.Backend.Services
{
    public class OptionData
    {
        /// <summary>
        /// Returns option content
        /// </summary>
        /// <param name="option">Option to retrieve</param>
        /// <param name="requestData">requested Data</param>
        /// <returns>Option content</returns>
        public static byte[] GetOptionData(DHCPOption option, DHCPMessage.Packet requestData)
        {
            int DHCPId = 0;
            byte DDataID, DataLength = 0;
            byte[] dumpData;

            DHCPId = (int)option;
            for (int i = 0; i < requestData.options.Length; i++)
            {
                DDataID = requestData.options[i];
                if (DDataID == (byte)DHCPOption.END_Option) break;
                if (DDataID == 0) continue;
                if (i + 1 >= requestData.options.Length || requestData.options[i + 1] > requestData.options.Length - i - 2) return null;
                if (DDataID == DHCPId)
                {
                    DataLength = requestData.options[i + 1];
                    dumpData = new byte[DataLength];
                    Array.Copy(requestData.options, i + 2, dumpData, 0, DataLength);
                    return dumpData;
                }
                else
                {
                    DataLength = requestData.options[i + 1];
                    i += 1 + DataLength;
                }
            }

            return null;
        }        
    }

    /// <summary>DHCP option enum</summary>
    public enum DHCPOption
    {
        /// <summary>Option 53</summary>
        DHCPMessageTYPE = 53,
        /// <summary>Option 54</summary>
        ServerIdentifier = 54,
        /// <summary>Option 60</summary>
        Vendorclassidentifier = 60,
        /// <summary>Option 82</summary>
        RelayInfo = 82,
        /// <summary>Option 93</summary>
        ClientSystemArchitecture = 93,
        /// <summary>Option 252</summary>
        Wpad = 252,
        /// <summary>Option 255 (END option)</summary>
        END_Option = 255
    }
}
