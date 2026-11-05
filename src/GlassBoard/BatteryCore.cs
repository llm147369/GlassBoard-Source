using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;

namespace GlassBoard;

// 灵动岛电量检测。
//
// 范围与限制：
// - 电脑电池：SystemInformation.PowerStatus（Windows 系统接口）。台式机/无电池设备返回
//   NoSystemBattery 或未知时视为“无有效数据”，界面完全隐藏，绝不猜测。
// - 鼠标/键盘（HID）：枚举 HID 类设备（SetupAPI），遍历设备全部链接集合，分类顶层集合
//   UsagePage=0x0001、Usage=0x0002(鼠标)/0x0006(键盘)，再在任意集合中读取电池强度 usage
//   （UsagePage=0x0006 Usage=0x0020，即 HID 标准电池服务），覆盖多集合接收器。
// - 鼠标/键盘（蓝牙 LE）：Windows 为带标准电池服务的外设创建电池设备接口，用
//   Battery.GetDeviceSelector 枚举后按蓝牙外观（Appearance）区分鼠标/键盘；
//   接口无容量数据时直接读 GATT 0x180F/0x2A19。参考 DockHub 的开源方案
//   （github.com/sametgurtuna/DockHub docs/battery-devices.md）。
//   仅当设备确实公开有效电量（0..100）时才显示；未公开的 HID/蓝牙设备不显示，绝不估算。
//   Logitech 2.4G 接收器（Unifying/Bolt）使用私有 HID++ 协议、不公开标准电池 usage，
//   属已知限制（参考 LGSTrayBattery/Solaar）；Logitech 蓝牙模式仍可读取。
// - 轮询周期 60 秒，绝不高频扫描；读取在独立后台线程执行，失败静默降级，
//   不影响设备连接与主界面。
// - 设备从枚举中消失（断开/卸载）后，对应数据在下一轮自动清除。
public static class BatteryCore
{
	public sealed class BatteryState
	{
		public double? PcLevel;
		public bool? PcCharging;
		public double? MouseLevel;
		public string MouseName;
		public double? KeyboardLevel;
		public string KeyboardName;

		public bool EqualsState(BatteryState other)
		{
			return other != null
				&& PcLevel == other.PcLevel
				&& PcCharging == other.PcCharging
				&& MouseLevel == other.MouseLevel
				&& MouseName == other.MouseName
				&& KeyboardLevel == other.KeyboardLevel
				&& KeyboardName == other.KeyboardName;
		}

		public BatteryState Copy()
		{
			return new BatteryState
			{
				PcLevel = PcLevel,
				PcCharging = PcCharging,
				MouseLevel = MouseLevel,
				MouseName = MouseName,
				KeyboardLevel = KeyboardLevel,
				KeyboardName = KeyboardName
			};
		}
	}

	public static volatile BatteryState Current = new BatteryState();

	// 在 UI 线程触发；订阅方自行节流刷新。
	public static event Action Changed;

	private static Thread worker;
	private static volatile bool running;
	private static readonly ManualResetEventSlim WakeSignal = new ManualResetEventSlim(false);
	private static readonly object Gate = new object();

	public static void Start(Dispatcher dispatcher)
	{
		lock (Gate)
		{
			if (worker != null)
			{
				return;
			}
			running = true;
			worker = new Thread(() => Loop(dispatcher))
			{
				IsBackground = true,
				Name = "GlassBoard.BatteryCore"
			};
			worker.Start();
		}
		SystemEvents.PowerModeChanged += OnPowerModeChanged;
	}

	public static void Stop()
	{
		SystemEvents.PowerModeChanged -= OnPowerModeChanged;
		lock (Gate)
		{
			running = false;
			WakeSignal.Set();
			worker = null;
		}
	}

	private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
	{
		// 电源状态变化（插拔电源/恢复）时立即重读一次。
		if (e.Mode == PowerModes.StatusChange || e.Mode == PowerModes.Resume)
		{
			WakeSignal.Set();
		}
	}

	private static void Loop(Dispatcher dispatcher)
	{
		while (running)
		{
			BatteryState next = ReadAll();
			BatteryState prev = Current;
			if (!next.EqualsState(prev))
			{
				Current = next;
				try
				{
					dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
					{
						Changed?.Invoke();
					}));
				}
				catch
				{
					// 窗口已关闭时静默。
				}
			}
			WakeSignal.Wait(TimeSpan.FromSeconds(60));
			WakeSignal.Reset();
		}
	}

	private static BatteryState ReadAll()
	{
		BatteryState state = new BatteryState();
		state.PcLevel = ReadPc(out bool charging);
		if (state.PcLevel.HasValue)
		{
			state.PcCharging = charging;
		}
		state.MouseLevel = ReadHidBattery(true, out string mouseName);
		if (!state.MouseLevel.HasValue)
		{
			state.MouseLevel = ReadBleBattery(true, out mouseName);
		}
		if (state.MouseLevel.HasValue)
		{
			state.MouseName = mouseName;
		}
		state.KeyboardLevel = ReadHidBattery(false, out string keyboardName);
		if (!state.KeyboardLevel.HasValue)
		{
			state.KeyboardLevel = ReadBleBattery(false, out keyboardName);
		}
		if (state.KeyboardLevel.HasValue)
		{
			state.KeyboardName = keyboardName;
		}
		return state;
	}

	// —— 电脑电池 ——

	public static double? ReadPc(out bool charging)
	{
		charging = false;
		try
		{
			PowerStatus ps = SystemInformation.PowerStatus;
			BatteryChargeStatus status = ps.BatteryChargeStatus;
			if (status == BatteryChargeStatus.NoSystemBattery || status == BatteryChargeStatus.Unknown)
			{
				return null; // 无内置电池或系统未提供数据
			}
			float fraction = ps.BatteryLifePercent;
			if (!(fraction >= 0f && fraction <= 1f))
			{
				return null;
			}
			charging = status == BatteryChargeStatus.Charging || ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
			return Math.Round(fraction * 100.0);
		}
		catch
		{
			return null;
		}
	}

	// —— 鼠标 / 键盘（HID 标准电池服务：UsagePage 0x0006, Usage 0x0020） ——

	private static readonly Guid HidGuid = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

	private const uint DigcfPresent = 0x00000002;
	private const uint DigcfDeviceInterface = 0x00000010;
	private const uint GenericRead = 0x80000000;
	private const uint GenericWrite = 0x40000000;
	private const uint FileShareReadWrite = 0x00000003;
	private const uint OpenExisting = 3;
	private const uint FileFlagOverlapped = 0x40000000;
	private const int SpdrpDeviceDesc = 0x00000000;
	private const int SpdrpFriendlyName = 0x0000000C;

	[StructLayout(LayoutKind.Sequential)]
	private struct SP_DEVICE_INTERFACE_DATA
	{
		public int cbSize;
		public Guid InterfaceClassGuid;
		public uint Flags;
		public IntPtr Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SP_DEVINFO_DATA
	{
		public int cbSize;
		public Guid ClassGuid;
		public uint DevInst;
		public IntPtr Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct HIDP_CAPS
	{
		public ushort Usage;
		public ushort UsagePage;
		public ushort InputReportByteLength;
		public ushort OutputReportByteLength;
		public ushort FeatureReportByteLength;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
		public ushort[] Reserved;
		public ushort NumberLinkCollectionNodes;
		public ushort NumberInputButtonCaps;
		public ushort NumberInputValueCaps;
		public ushort NumberInputDataIndices;
		public ushort NumberOutputButtonCaps;
		public ushort NumberOutputValueCaps;
		public ushort NumberOutputDataIndices;
		public ushort NumberFeatureButtonCaps;
		public ushort NumberFeatureValueCaps;
		public ushort NumberFeatureDataIndices;
	}

	[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

	[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

	[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, ref uint RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

	[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, ref uint PropertyRegDataType, byte[] PropertyBuffer, uint PropertyBufferSize, ref uint RequiredSize);

	[DllImport("setupapi.dll", SetLastError = true)]
	private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	[DllImport("hid.dll", SetLastError = true)]
	private static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);

	[DllImport("hid.dll", SetLastError = true)]
	private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

	[DllImport("hid.dll", SetLastError = true)]
	private static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

	// ReportType: 0=Input 1=Output 2=Feature
	[DllImport("hid.dll", SetLastError = true)]
	private static extern int HidP_GetSpecificValueCaps(int ReportType, ushort UsagePage, ushort LinkCollection, ushort Usage, byte[] ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);

	[DllImport("hid.dll", SetLastError = true)]
	private static extern bool HidD_GetFeature(IntPtr HidDeviceObject, byte[] ReportBuffer, uint ReportBufferLength);

	[DllImport("hid.dll", SetLastError = true)]
	private static extern bool HidD_GetInputReport(IntPtr HidDeviceObject, byte[] ReportBuffer, uint ReportBufferLength);

	[DllImport("hid.dll", SetLastError = true)]
	private static extern int HidP_GetUsageValue(int ReportType, ushort UsagePage, ushort LinkCollection, ushort Usage, out uint UsageValue, IntPtr PreparsedData, byte[] Report, uint ReportLength);

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private struct HIDP_LINK_COLLECTION_NODE
	{
		public ushort LinkUsage;
		public ushort LinkUsagePage;
		public ushort Parent;
		public ushort NumberOfChildren;
		public ushort NextSibling;
		public ushort FirstChild;
		public uint CollectionType;
		public IntPtr IsAlias;
		public IntPtr Reserved;
	}

	[DllImport("hid.dll", SetLastError = true)]
	private static extern int HidP_GetLinkCollectionNodes(HIDP_LINK_COLLECTION_NODE[] LinkCollectionNodes, ref uint LinkCollectionNodesLength, IntPtr PreparsedData);

	private const uint StatusSuccess = 0x00000000;

	public static double? ReadHidBattery(bool wantMouse, out string deviceName)
	{
		deviceName = null;
		try
		{
			Guid hid = HidGuid;
			IntPtr set = SetupDiGetClassDevsW(ref hid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
			if (set == new IntPtr(-1))
			{
				return null;
			}
			try
			{
				uint index = 0;
				while (true)
				{
					SP_DEVICE_INTERFACE_DATA iface = default(SP_DEVICE_INTERFACE_DATA);
					iface.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
					if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index++, ref iface))
					{
						break; // 枚举结束或失败
					}
					uint needed = 0;
					SP_DEVINFO_DATA devData = default(SP_DEVINFO_DATA);
					devData.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
					SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, ref needed, ref devData);
					if (needed == 0 || needed > 4096)
					{
						continue;
					}
					IntPtr detail = Marshal.AllocHGlobal((int)needed);
					try
					{
						Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
						SP_DEVINFO_DATA dev = default(SP_DEVINFO_DATA);
						dev.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
						if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, needed, ref needed, ref dev))
						{
							continue;
						}
						string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
						if (string.IsNullOrEmpty(path))
						{
							continue;
						}
						double? level = ReadDeviceBattery(path, wantMouse, out bool ok);
						if (ok && level.HasValue)
						{
							deviceName = ReadDeviceName(set, ref dev) ?? "HID 设备";
							return level;
						}
					}
					finally
					{
						Marshal.FreeHGlobal(detail);
					}
				}
			}
			finally
			{
				SetupDiDestroyDeviceInfoList(set);
			}
		}
		catch
		{
			// 静默：读不到就当设备没暴露电量。
		}
		return null;
	}

	// —— 蓝牙 LE 电量（Windows 电池设备接口 + GATT 0x180F/0x2A19 直读）——
	// Windows 会为带标准电池服务的外设创建设备接口（System.Devices.Aep.DeviceAddress 提供
	// 蓝牙 MAC）；先用 Battery.FromIdAsync 拿系统聚合值（满充/剩余容量百分比），接口无
	// 容量数据时直读 GATT Battery Level。用蓝牙外观（BT SIG Appearance）区分鼠标/键盘，
	// 未分类或非 HID 外观的蓝牙设备一律跳过，绝不猜测。参考 DockHub 开源方案
	// （github.com/sametgurtuna/DockHub, docs/battery-devices.md）。
	// 失败缓存 5 分钟，避免每 60 秒重复枚举加重蓝牙栈负担。

	private static DateTime bleMouseFailAt = DateTime.MinValue;
	private static DateTime bleKeyboardFailAt = DateTime.MinValue;

	private static double? ReadBleBattery(bool wantMouse, out string deviceName)
	{
		deviceName = null;
		DateTime failAt = wantMouse ? bleMouseFailAt : bleKeyboardFailAt;
		if ((DateTime.UtcNow - failAt).TotalMinutes < 5)
		{
			return null;
		}
		try
		{
			Task<(double? Level, string Name)> task = ReadBleBatteryAsync(wantMouse);
			if (!task.Wait(20000))
			{
				if (wantMouse)
				{
					bleMouseFailAt = DateTime.UtcNow;
				}
				else
				{
					bleKeyboardFailAt = DateTime.UtcNow;
				}
				return null;
			}
			(double? Level, string Name) result = task.Result;
			if (result.Level.HasValue)
			{
				deviceName = result.Name;
				return result.Level;
			}
			if (wantMouse)
			{
				bleMouseFailAt = DateTime.UtcNow;
			}
			else
			{
				bleKeyboardFailAt = DateTime.UtcNow;
			}
			return null;
		}
		catch
		{
			if (wantMouse)
			{
				bleMouseFailAt = DateTime.UtcNow;
			}
			else
			{
				bleKeyboardFailAt = DateTime.UtcNow;
			}
			return null;
		}
	}

	private static async Task<(double? Level, string Name)> ReadBleBatteryAsync(bool wantMouse)
	{
		DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(Battery.GetDeviceSelector());
		foreach (DeviceInformation di in devices)
		{
			if (!di.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object macObj))
			{
				continue;
			}
			string mac = ((macObj as string) ?? string.Empty).Replace(":", "").Replace("-", "").ToUpperInvariant();
			if (mac.Length < 12)
			{
				continue;
			}
			ulong address;
			try
			{
				address = Convert.ToUInt64(mac, 16);
			}
			catch
			{
				continue;
			}
			BluetoothLEDevice led = null;
			try
			{
				led = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
				if (led == null)
				{
					continue;
				}
				// BT SIG Appearance：HID 鼠标 0x03C2、键盘 0x03C1（低 10 位比较）
				ushort raw = led.Appearance.RawValue;
				bool isMouse = (raw & 0x03FF) == 0x03C2;
				bool isKeyboard = (raw & 0x03FF) == 0x03C1;
				if (wantMouse ? !isMouse : !isKeyboard)
				{
					continue;
				}
				string name = string.IsNullOrWhiteSpace(led.Name) ? null : led.Name;
				Battery bat = await Battery.FromIdAsync(di.Id);
				if (bat != null)
				{
					BatteryReport report = bat.GetReport();
					if (report.Status != Windows.System.Power.BatteryStatus.NotPresent
						&& report.FullChargeCapacityInMilliwattHours.HasValue
						&& report.RemainingCapacityInMilliwattHours.HasValue
						&& report.FullChargeCapacityInMilliwattHours.Value > 0)
					{
						double pct = Math.Round((double)report.RemainingCapacityInMilliwattHours.Value / report.FullChargeCapacityInMilliwattHours.Value * 100.0);
						if (pct >= 0 && pct <= 100)
						{
							return (pct, name);
						}
					}
				}
				GattDeviceServicesResult services = await led.GetGattServicesAsync(BluetoothCacheMode.Uncached);
				if (services.Status == GattCommunicationStatus.Success)
				{
					foreach (GattDeviceService svc in services.Services)
					{
						if (svc.Uuid != GattServiceUuids.Battery)
						{
							continue;
						}
						GattCharacteristicsResult chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
						if (chars.Status != GattCommunicationStatus.Success)
						{
							continue;
						}
						foreach (GattCharacteristic ch in chars.Characteristics)
						{
							if (ch.Uuid != GattCharacteristicUuids.BatteryLevel)
							{
								continue;
							}
							GattReadResult read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
							if (read.Status == GattCommunicationStatus.Success && read.Value != null)
							{
								byte[] value = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(read.Value);
								if (value.Length > 0)
								{
									byte level = value[0];
									if (level <= 100)
									{
										return (level, name);
									}
								}
							}
						}
					}
				}
			}
			catch
			{
				// 单个设备失败继续下一个
			}
			finally
			{
				if (led != null)
				{
					try
					{
						led.Dispose();
					}
					catch
					{
					}
				}
			}
		}
		return (null, null);
	}

	private static double? ReadDeviceBattery(string path, bool wantMouse, out bool ok)
	{
		ok = false;
		IntPtr handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
		if (handle == new IntPtr(-1))
		{
			return null;
		}
		try
		{
			if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
			{
				return null;
			}
			try
			{
				if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) != StatusSuccess)
				{
					return null;
				}
				ushort wantUsage = wantMouse ? (ushort)0x0002 : (ushort)0x0006;
				// 枚举全部链接集合：任一集合是鼠标/键盘即视为目标设备（覆盖多集合接收器，
				// 例如键盘+鼠标二合一接收器，其鼠标集合不一定是集合 0）。
				uint count = (uint)Math.Max(1, (int)caps.NumberLinkCollectionNodes);
				HIDP_LINK_COLLECTION_NODE[] nodes = new HIDP_LINK_COLLECTION_NODE[count];
				bool matches = HidP_GetLinkCollectionNodes(nodes, ref count, preparsed) == StatusSuccess && count > 0;
				bool isTarget = false;
				if (matches)
				{
					for (int i = 0; i < (int)Math.Min(count, nodes.Length); i++)
					{
						if (nodes[i].LinkUsagePage == 0x0001 && nodes[i].LinkUsage == wantUsage)
						{
							isTarget = true;
							break;
						}
					}
				}
				if (!isTarget && !(caps.UsagePage == 0x0001 && caps.Usage == wantUsage))
				{
					return null; // 没有任何集合是目标设备类型
				}
				// 逐个集合查找电池强度 usage（先 Feature 后 Input）：部分接收器把电池
				// 放在非顶层集合里，固定 LinkCollection=0 会漏掉。
				int maxCollections = matches ? (int)Math.Min(count, nodes.Length) : 1;
				for (int c = 0; c < maxCollections; c++)
				{
					double? level = ReadBatteryUsage(2, caps.FeatureReportByteLength, handle, preparsed, (ushort)c);
					if (level.HasValue)
					{
						ok = true;
						return level;
					}
				}
				for (int c = 0; c < maxCollections; c++)
				{
					double? level = ReadBatteryUsageGuarded(0, caps.InputReportByteLength, handle, preparsed, (ushort)c);
					if (level.HasValue)
					{
						ok = true;
						return level;
					}
				}
			}
			finally
			{
				HidD_FreePreparsedData(preparsed);
			}
		}
		finally
		{
			CloseHandle(handle);
		}
		return null;
	}

	private static double? ReadBatteryUsage(int reportType, ushort reportByteLength, IntPtr handle, IntPtr preparsed, ushort linkCollection)
	{
		try
		{
			ushort len = 80;
			byte[] capsBuf = new byte[80];
			if (HidP_GetSpecificValueCaps(reportType, 0x0006, linkCollection, 0x0020, capsBuf, ref len, preparsed) != StatusSuccess || len < 3)
			{
				return null;
			}
			byte reportId = capsBuf[2];
			if (reportId == 0)
			{
				reportId = 1;
			}
			uint reportLen = reportByteLength;
			if (reportLen == 0)
			{
				reportLen = 128;
			}
			byte[] report = new byte[reportLen];
			report[0] = reportId;
			bool readOk = reportType == 2
				? HidD_GetFeature(handle, report, reportLen)
				: HidD_GetInputReport(handle, report, reportLen);
			if (!readOk)
			{
				return null;
			}
			if (HidP_GetUsageValue(reportType, 0x0006, linkCollection, 0x0020, out uint value, preparsed, report, reportLen) != StatusSuccess)
			{
				return null;
			}
			if (value > 100)
			{
				return null;
			}
			return value;
		}
		catch
		{
			return null;
		}
	}

	// Input 报表读取个别设备可能不回包：放到可放弃的任务里，1500ms 超时后放弃该设备。
	private static double? ReadBatteryUsageGuarded(int reportType, ushort reportByteLength, IntPtr handle, IntPtr preparsed, ushort linkCollection)
	{
		System.Threading.Tasks.Task<double?> task = System.Threading.Tasks.Task.Run(() => ReadBatteryUsage(reportType, reportByteLength, handle, preparsed, linkCollection));
		if (task.Wait(1500))
		{
			return task.Result;
		}
		return null;
	}

	private static string ReadDeviceName(IntPtr set, ref SP_DEVINFO_DATA dev)
	{
		try
		{
			string[] props = { null };
			uint regType = 0;
			uint needed = 0;
			byte[] buffer = new byte[512];
			if (SetupDiGetDeviceRegistryPropertyW(set, ref dev, SpdrpFriendlyName, ref regType, buffer, (uint)buffer.Length, ref needed) && needed > 0)
			{
				string name = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length)).TrimEnd('\0');
				if (!string.IsNullOrWhiteSpace(name))
				{
					return name;
				}
			}
			regType = 0;
			needed = 0;
			if (SetupDiGetDeviceRegistryPropertyW(set, ref dev, SpdrpDeviceDesc, ref regType, buffer, (uint)buffer.Length, ref needed) && needed > 0)
			{
				string name = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length)).TrimEnd('\0');
				if (!string.IsNullOrWhiteSpace(name))
				{
					return name;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	// 说明检测范围与限制，用于设置/诊断信息。
	public static string Diagnostic()
	{
		return "电脑电池：Windows 系统电源状态（无内置电池时自动隐藏）。"
			+ "鼠标/键盘：HID 标准电池服务（Battery Strength，遍历设备全部链接集合，覆盖多集合接收器）"
			+ "+ 蓝牙 LE 标准电池服务（Windows 电池接口 / GATT 0x180F-0x2A19，按蓝牙外观识别鼠标键盘）。"
			+ "仅显示设备/驱动向系统公开的有效电量（0-100）；使用私有协议的 2.4G 接收器"
			+ "（如 Logitech 优联/Bolt）属已知限制，不显示也不估算。"
			+ "检测周期 60 秒，断开或卸载设备后自动清除对应项目。";
	}
}
