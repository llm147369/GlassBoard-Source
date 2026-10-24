using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBoard;

// WLAN（wlanapi）网络扫描与连接。扫描/断开无需管理员权限；
// 连接新网络通过"每用户配置文件"（WLAN_PROFILE_USER）实现，同样无需提权。
public static class WirelessCore
{
	public sealed class WifiNetwork
	{
		public string Ssid;
		public int Signal;
		public bool Secured;
		public bool Connected;
	}

	private enum DOT11_BSS_TYPE
	{
		Infrastructure = 1,
		Independent = 2,
		Any = 3
	}

	private enum WLAN_CONNECTION_MODE
	{
		Profile = 0,
		TemporaryProfile = 1,
		DiscoverySecure = 2,
		DiscoveryUnsecure = 3,
		Auto = 4,
		Invalid = 5
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DOT11_SSID
	{
		public uint uSSIDLength;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
		public byte[] ucSSID;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WLAN_INTERFACE_INFO
	{
		public Guid InterfaceGuid;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string strInterfaceDescription;

		public uint isState;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WLAN_INTERFACE_INFO_LIST
	{
		public uint dwNumberOfItems;
		public uint dwIndex;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WLAN_AVAILABLE_NETWORK
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string strProfileName;

		public DOT11_SSID dot11Ssid;
		public DOT11_BSS_TYPE dot11BssType;
		public uint uNumberOfBssids;

		[MarshalAs(UnmanagedType.Bool)]
		public bool bNetworkConnectable;

		public uint wlanNotConnectableReason;
		public uint uNumberOfPhyTypes;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
		public DOT11_BSS_TYPE[] dot11PhyTypes;

		[MarshalAs(UnmanagedType.Bool)]
		public bool bMorePhyTypes;

		public uint wlanSignalQuality;

		[MarshalAs(UnmanagedType.Bool)]
		public bool bSecurityEnabled;

		public uint dot11DefaultAuthAlgorithm;
		public uint dot11DefaultCipherAlgorithm;
		public uint dwFlags;
		public uint dwNotConnectableReason;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WLAN_AVAILABLE_NETWORK_LIST
	{
		public uint dwNumberOfItems;
		public uint dwIndex;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WLAN_PROFILE_INFO
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string strProfileName;

		public uint dwFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WLAN_PROFILE_INFO_LIST
	{
		public uint dwNumberOfItems;
		public uint dwIndex;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WLAN_CONNECTION_PARAMETERS
	{
		public WLAN_CONNECTION_MODE wlanConnectionMode;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string strProfile;

		public IntPtr pDot11Ssid;
		public IntPtr pDesiredBssidList;
		public DOT11_BSS_TYPE dot11BssType;
		public uint dwFlags;
	}

	[DllImport("wlanapi.dll")]
	private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr dot11Ssid, IntPtr data, IntPtr reserved);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr availableNetworkList);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanGetProfile(IntPtr clientHandle, ref Guid interfaceGuid, [MarshalAs(UnmanagedType.LPWStr)] string profileName, IntPtr reserved, out IntPtr profileXml, ref uint flags, out uint grantedAccess);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanSetProfile(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string profileXml, string allUserProfileSecurity, [MarshalAs(UnmanagedType.Bool)] bool overwrite, IntPtr reserved, out uint reasonCode);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid, ref WLAN_CONNECTION_PARAMETERS parameters, IntPtr reserved);

	[DllImport("wlanapi.dll")]
	private static extern uint WlanDisconnect(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved);

	[DllImport("wlanapi.dll")]
	private static extern void WlanFreeMemory(IntPtr memory);

	// WlanSetProfile 的存储位置标志：0x2=当前用户（无需管理员权限），0x4=所有用户（需提权）
	private const uint WLAN_PROFILE_USER = 2u;
	private const uint WLAN_AVAILABLE_NETWORK_CONNECTED = 1u;
	private const uint ERROR_SUCCESS = 0u;

	private static IntPtr Open(out Guid iface)
	{
		iface = Guid.Empty;
		if (WlanOpenHandle(2u, IntPtr.Zero, out uint negotiated, out IntPtr client) != 0)
		{
			throw new InvalidOperationException("无法打开 WLAN 服务");
		}
		try
		{
			uint result = WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr listPtr);
			if (result != 0 || listPtr == IntPtr.Zero)
			{
				throw new InvalidOperationException("未找到可用的无线网卡");
			}
			try
			{
				WLAN_INTERFACE_INFO_LIST header = (WLAN_INTERFACE_INFO_LIST)Marshal.PtrToStructure(listPtr, typeof(WLAN_INTERFACE_INFO_LIST));
				if (header.dwNumberOfItems == 0)
				{
					throw new InvalidOperationException("未找到可用的无线网卡");
				}
				int size = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
				WLAN_INTERFACE_INFO info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(IntPtr.Add(listPtr, 8), typeof(WLAN_INTERFACE_INFO));
				iface = info.InterfaceGuid;
			}
			finally
			{
				WlanFreeMemory(listPtr);
			}
			return client;
		}
		catch
		{
			WlanCloseHandle(client, IntPtr.Zero);
			throw;
		}
	}

	public static List<WifiNetwork> Networks()
	{
		List<WifiNetwork> list = new List<WifiNetwork>();
		IntPtr client = Open(out Guid iface);
		try
		{
			uint result = WlanGetAvailableNetworkList(client, ref iface, 2u, IntPtr.Zero, out IntPtr listPtr);
			if (result != 0 || listPtr == IntPtr.Zero)
			{
				return list;
			}
			try
			{
				WLAN_AVAILABLE_NETWORK_LIST header = (WLAN_AVAILABLE_NETWORK_LIST)Marshal.PtrToStructure(listPtr, typeof(WLAN_AVAILABLE_NETWORK_LIST));
				int size = Marshal.SizeOf(typeof(WLAN_AVAILABLE_NETWORK));
				for (uint i = 0; i < header.dwNumberOfItems; i++)
				{
					WLAN_AVAILABLE_NETWORK network = (WLAN_AVAILABLE_NETWORK)Marshal.PtrToStructure(IntPtr.Add(listPtr, 8 + (int)(i * (uint)size)), typeof(WLAN_AVAILABLE_NETWORK));
					byte[] bytes = network.dot11Ssid.ucSSID ?? new byte[0];
					int length = Math.Max(0, Math.Min(bytes.Length, (int)network.dot11Ssid.uSSIDLength));
					string ssid = Encoding.UTF8.GetString(bytes, 0, length);
					ssid = ssid.TrimEnd('\0');
					if (string.IsNullOrEmpty(ssid))
					{
						continue;
					}
					list.Add(new WifiNetwork
					{
						Ssid = ssid,
						Signal = (int)network.wlanSignalQuality,
						Secured = network.bSecurityEnabled,
						Connected = (network.dwFlags & WLAN_AVAILABLE_NETWORK_CONNECTED) != 0
					});
				}
			}
			finally
			{
				WlanFreeMemory(listPtr);
			}
		}
		finally
		{
			WlanCloseHandle(client, IntPtr.Zero);
		}
		// 同一 SSID 可能来自多个 BSSID（双频路由器/中继在 2.4G 与 5G 各广播一次），
		// 和系统自带列表一样合并成一行：已连接的优先，否则取信号最强
		List<WifiNetwork> merged = new List<WifiNetwork>();
		foreach (IGrouping<string, WifiNetwork> group in list.GroupBy((WifiNetwork n) => n.Ssid, StringComparer.Ordinal))
		{
			WifiNetwork best = null;
			foreach (WifiNetwork n in group)
			{
				if (best == null || (n.Connected && !best.Connected) || (n.Connected == best.Connected && n.Signal > best.Signal))
				{
					best = n;
				}
			}
			merged.Add(best);
		}
		merged.Sort((WifiNetwork a, WifiNetwork b) => (b.Signal != a.Signal) ? b.Signal.CompareTo(a.Signal) : string.Compare(a.Ssid, b.Ssid, StringComparison.OrdinalIgnoreCase));
		return merged;
	}

	public static void Scan()
	{
		IntPtr client = Open(out Guid iface);
		try
		{
			WlanScan(client, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		}
		finally
		{
			WlanCloseHandle(client, IntPtr.Zero);
		}
	}

	private static string ParseProfileSsid(string xml)
	{
		if (string.IsNullOrEmpty(xml))
		{
			return null;
		}
		int ssidStart = xml.IndexOf("<SSID>", StringComparison.Ordinal);
		int ssidEnd = xml.IndexOf("</SSID>", ssidStart + 6, StringComparison.Ordinal);
		if (ssidStart < 0 || ssidEnd < 0)
		{
			return null;
		}
		string block = xml.Substring(ssidStart + 6, ssidEnd - ssidStart - 6);
		int nameStart = block.IndexOf("<name>", StringComparison.Ordinal);
		if (nameStart >= 0)
		{
			int nameEnd = block.IndexOf("</name>", StringComparison.Ordinal);
			if (nameEnd > nameStart)
			{
				return block.Substring(nameStart + 6, nameEnd - nameStart - 6);
			}
		}
		int hexStart = block.IndexOf("<hex>", StringComparison.Ordinal);
		if (hexStart >= 0)
		{
			int hexEnd = block.IndexOf("</hex>", StringComparison.Ordinal);
			if (hexEnd > hexStart)
			{
				string hex = block.Substring(hexStart + 5, hexEnd - hexStart - 5);
				try
				{
					byte[] bytes = Enumerable.Range(0, hex.Length / 2).Select((int i) => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
					return Encoding.UTF8.GetString(bytes);
				}
				catch
				{
					return null;
				}
			}
		}
		return null;
	}

	private static void ForEachProfile(IntPtr client, ref Guid iface, Func<string, string, bool> visit)
	{
		uint result = WlanGetProfileList(client, ref iface, IntPtr.Zero, out IntPtr listPtr);
		if (result != ERROR_SUCCESS || listPtr == IntPtr.Zero)
		{
			return;
		}
		try
		{
			WLAN_PROFILE_INFO_LIST header = (WLAN_PROFILE_INFO_LIST)Marshal.PtrToStructure(listPtr, typeof(WLAN_PROFILE_INFO_LIST));
			int size = Marshal.SizeOf(typeof(WLAN_PROFILE_INFO));
			for (uint i = 0; i < header.dwNumberOfItems; i++)
			{
				WLAN_PROFILE_INFO info = (WLAN_PROFILE_INFO)Marshal.PtrToStructure(IntPtr.Add(listPtr, 8 + (int)(i * (uint)size)), typeof(WLAN_PROFILE_INFO));
				if (string.IsNullOrEmpty(info.strProfileName))
				{
					continue;
				}
				string ssid = null;
				try
				{
					uint flags = 0u;
					if (WlanGetProfile(client, ref iface, info.strProfileName, IntPtr.Zero, out IntPtr xmlPtr, ref flags, out uint access) == ERROR_SUCCESS && xmlPtr != IntPtr.Zero)
					{
						try
						{
							ssid = ParseProfileSsid(Marshal.PtrToStringUni(xmlPtr) ?? "");
						}
						finally
						{
							WlanFreeMemory(xmlPtr);
						}
					}
				}
				catch
				{
				}
				if (ssid != null && !visit(info.strProfileName, ssid))
				{
					break;
				}
			}
		}
		finally
		{
			WlanFreeMemory(listPtr);
		}
	}

	// 已保存过的网络 SSID 集合（解析配置 XML 还原真实 SSID，中文/特殊字符 SSID 也能正确匹配）
	public static HashSet<string> KnownProfiles()
	{
		HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
		IntPtr client = Open(out Guid iface);
		try
		{
			ForEachProfile(client, ref iface, (string name, string ssid) =>
			{
				set.Add(ssid);
				return true;
			});
		}
		finally
		{
			WlanCloseHandle(client, IntPtr.Zero);
		}
		return set;
	}

	// 按真实 SSID 找到系统里对应的配置文件名称（切换连接用）
	private static string FindProfileName(IntPtr client, ref Guid iface, string target)
	{
		string found = null;
		ForEachProfile(client, ref iface, (string name, string ssid) =>
		{
			if (ssid == target)
			{
				found = name;
				return false;
			}
			return true;
		});
		return found;
	}

	private static bool IsPrintableAscii(string s)
	{
		foreach (char c in s)
		{
			if (c < ' ' || c > '~')
			{
				return false;
			}
		}
		return true;
	}

	private static string EscapeXml(string s)
	{
		return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
	}

	private static string ProfileXml(string ssid, string password, out string profileName)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(ssid);
		profileName = (IsPrintableAscii(ssid) ? ssid : ("GB-" + BitConverter.ToString(bytes).Replace("-", "")));
		string ssidTag;
		if (IsPrintableAscii(ssid))
		{
			ssidTag = "<name>" + EscapeXml(ssid) + "</name>";
		}
		else
		{
			StringBuilder hex = new StringBuilder();
			foreach (byte b in bytes)
			{
				hex.Append(b.ToString("X2"));
			}
			ssidTag = "<hex>" + hex + "</hex>";
		}
		bool secured = !string.IsNullOrEmpty(password);
		StringBuilder sb = new StringBuilder();
		sb.Append("<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">");
		sb.Append("<name>").Append(EscapeXml(profileName)).Append("</name>");
		sb.Append("<SSIDConfig><SSID>").Append(ssidTag).Append("</SSID></SSIDConfig>");
		sb.Append("<connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security>");
		if (secured)
		{
			sb.Append("<authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>");
			sb.Append("<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>").Append(EscapeXml(password)).Append("</keyMaterial></sharedKey>");
		}
		else
		{
			sb.Append("<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>");
		}
		sb.Append("</security></MSM></WLANProfile>");
		return sb.ToString();
	}

	// password 为 null/空 表示开放式网络；为 null 时若存在已保存配置则按 SSID 直连（连接过的网络免密码）
	public static string Connect(string ssid, string password)
	{
		IntPtr client = Open(out Guid iface);
		try
		{
			if (password == null)
			{
				// 连接已保存的网络：找到系统配置文件并按名称直连（可切换网络）
				string savedProfileName = FindProfileName(client, ref iface, ssid);
				if (string.IsNullOrEmpty(savedProfileName))
				{
					return "未找到「" + ssid + "」的已保存配置，请重新输入密码。";
				}
				WLAN_CONNECTION_PARAMETERS saved = default(WLAN_CONNECTION_PARAMETERS);
				saved.wlanConnectionMode = WLAN_CONNECTION_MODE.Profile;
				saved.strProfile = savedProfileName;
				saved.pDot11Ssid = IntPtr.Zero;
				saved.dot11BssType = DOT11_BSS_TYPE.Any;
				uint savedResult = WlanConnect(client, ref iface, ref saved, IntPtr.Zero);
				if (savedResult != ERROR_SUCCESS)
				{
					return "切换失败（代码 " + savedResult + "）";
				}
				return null;
			}
			string xml = ProfileXml(ssid, password, out string profileName);
			uint reason = 0u;
			uint setResult = WlanSetProfile(client, ref iface, WLAN_PROFILE_USER, xml, null, overwrite: true, IntPtr.Zero, out reason);
			if (setResult != ERROR_SUCCESS)
			{
				return "保存连接配置失败（代码 " + setResult + "）";
			}
			WLAN_CONNECTION_PARAMETERS parameters = default(WLAN_CONNECTION_PARAMETERS);
			parameters.wlanConnectionMode = WLAN_CONNECTION_MODE.Profile;
			parameters.strProfile = profileName;
			parameters.dot11BssType = DOT11_BSS_TYPE.Any;
			uint connectResult = WlanConnect(client, ref iface, ref parameters, IntPtr.Zero);
			if (connectResult != ERROR_SUCCESS)
			{
				return "连接失败（代码 " + connectResult + "）。请检查密码是否正确。";
			}
			return null;
		}
		finally
		{
			WlanCloseHandle(client, IntPtr.Zero);
		}
	}

	public static void Disconnect()
	{
		IntPtr client = Open(out Guid iface);
		try
		{
			WlanDisconnect(client, ref iface, IntPtr.Zero);
		}
		finally
		{
			WlanCloseHandle(client, IntPtr.Zero);
		}
	}
}
