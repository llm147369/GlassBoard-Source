using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GlassBoard;

// CoreAudio（WASAPI）音量/静音与输出设备管理，纯 P/Invoke + COM 互操作，无需管理员权限。
public static class AudioCore
{
	public sealed class AudioDevice
	{
		public string Id;
		public string Name;
		public bool IsDefault;
	}

	private enum EDataFlow
	{
		Render,
		Capture,
		All
	}

	private enum ERole
	{
		Console,
		Multimedia,
		Communications
	}

	[ComImport]
	[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		// out IntPtr：此系统上对 IMMDeviceCollection 的 QI 会失败（E_NOINTERFACE），
		// 因此这里只取裸指针，GetCount/Item 走裸虚表调用。
		int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

		int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

		int GetDevice(string id, out IMMDevice device);

		int RegisterEndpointNotificationCallback(IntPtr client);

		int UnregisterEndpointNotificationCallback(IntPtr client);
	}

	[ComImport]
	[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

		int OpenPropertyStore(uint access, out IntPtr properties);

		int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

		int GetState(out uint state);
	}

	[ComImport]
	[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioEndpointVolume
	{
		int RegisterControlChangeNotify(IntPtr client);

		int UnregisterControlChangeNotify(IntPtr client);

		int GetChannelCount(out uint count);

		int SetMasterVolumeLevel(float level, ref Guid context);

		int SetMasterVolumeLevelScalar(float level, ref Guid context);

		int GetMasterVolumeLevel(out float level);

		int GetMasterVolumeLevelScalar(out float level);

		int SetChannelVolumeLevel(uint channel, float level, ref Guid context);

		int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

		int GetChannelVolumeLevel(uint channel, out float level);

		int GetChannelVolumeLevelScalar(uint channel, out float level);

		int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

		int GetMute(out bool mute);

		int GetVolumeStepInfo(out uint step, out uint stepCount);

		int VolumeStepUp(ref Guid context);

		int VolumeStepDown(ref Guid context);

		int QueryHardwareSupport(out uint support);

		int GetVolumeRange(out float min, out float max, out float increment);
	}

	[ComImport]
	[Guid("870AF99C-171D-4F63-B0AF-E63DF44C7A2F")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPolicyConfig
	{
		int GetMixFormat(string deviceId, IntPtr format);

		int GetDeviceFormat(string deviceId, int isDefault, IntPtr format);

		int ResetDeviceFormat(string deviceId);

		int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

		int GetProcessingPeriod(string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minPeriod);

		int SetProcessingPeriod(string deviceId, IntPtr period);

		int GetShareMode(string deviceId, IntPtr mode);

		int SetShareMode(string deviceId, IntPtr mode);

		int GetPropertyValue(string deviceId, ref PROPERTYKEY key, out PROPVARIANT value);

		int SetPropertyValue(string deviceId, ref PROPERTYKEY key, ref PROPVARIANT value);

		int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

		int SetEndpointVisibility(string deviceId, int isVisible);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROPERTYKEY
	{
		public Guid fmtid;
		public uint pid;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct PROPVARIANT
	{
		[FieldOffset(0)]
		public ushort vt;

		[FieldOffset(8)]
		public IntPtr pointerValue;
	}

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PROPVARIANT value);

	[DllImport("ole32.dll")]
	private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsCtx, ref Guid iid, out IntPtr instance);

	private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

	private static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");

	private static readonly Guid CLSID_PolicyConfigClient = new Guid("294935CE-F637-4E7C-A41B-AB255460B862");

	private static readonly Guid IID_IPolicyConfig = new Guid("870AF99C-171D-4F63-B0AF-E63DF44C7A2F");

	private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

	private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
	{
		fmtid = new Guid("A45C254E-DF1C-4EFD-8024-67D146A850E0"),
		pid = 14
	};

	private static readonly PROPERTYKEY PKEY_Device_DeviceDesc = new PROPERTYKEY
	{
		fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
		pid = 2
	};

	private const uint CLSCTX_ALL = 23u;
	private const uint DEVICE_STATE_ACTIVE = 1u;

	// 裸虚表调用（StdCall）。IMMDeviceCollection 的 GetCount/Item 与
	// IPropertyStore 的 GetValue 在部分系统上无法通过 RCW QI 调用。
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int CollectionGetCount(IntPtr self, out uint count);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int CollectionItem(IntPtr self, uint index, out IntPtr device);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int StoreGetValue(IntPtr self, ref PROPERTYKEY key, out PROPVARIANT value);

	// 用 CoCreateInstance 显式创建 COM 对象，绕开 `new ComImportClass()` 在部分系统上
	// 对类接口（CLSID 当 IID）做 QI 失败导致 InvalidCastException 的问题。
	private static object CreateInstance(ref Guid clsid, ref Guid iid)
	{
		int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr p);
		if (hr < 0)
		{
			Marshal.ThrowExceptionForHR(hr);
		}
		return Marshal.GetObjectForIUnknown(p);
	}

	private static IMMDeviceEnumerator Enumerator()
	{
		Guid clsid = CLSID_MMDeviceEnumerator;
		Guid iid = IID_IMMDeviceEnumerator;
		return (IMMDeviceEnumerator)CreateInstance(ref clsid, ref iid);
	}

	private static IMMDevice DefaultEndpoint()
	{
		IMMDeviceEnumerator enumerator = Enumerator();
		enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out IMMDevice device);
		return device;
	}

	private static IAudioEndpointVolume VolumeOf(IMMDevice device)
	{
		Guid iid = IID_IAudioEndpointVolume;
		device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object instance);
		return (IAudioEndpointVolume)instance;
	}

	// 0-1
	public static bool TryGetVolume(out float volume, out bool muted)
	{
		try
		{
			IAudioEndpointVolume endpointVolume = VolumeOf(DefaultEndpoint());
			Guid empty = Guid.Empty;
			endpointVolume.GetMasterVolumeLevelScalar(out volume);
			endpointVolume.GetMute(out muted);
			return true;
		}
		catch
		{
			volume = 0.5f;
			muted = false;
			return false;
		}
	}

	public static bool TrySetVolume(float value)
	{
		try
		{
			Guid empty = Guid.Empty;
			VolumeOf(DefaultEndpoint()).SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, value)), ref empty);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static bool TrySetMute(bool mute)
	{
		try
		{
			Guid empty = Guid.Empty;
			VolumeOf(DefaultEndpoint()).SetMute(mute, ref empty);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static List<AudioDevice> Devices()
	{
		List<AudioDevice> list = new List<AudioDevice>();
		try
		{
			IMMDeviceEnumerator enumerator = Enumerator();
			enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out IMMDevice defaultDevice);
			string defaultId = null;
			try
			{
				defaultDevice.GetId(out defaultId);
			}
			catch
			{
			}
			int hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out IntPtr collection);
			if (hr < 0 || collection == IntPtr.Zero)
			{
				return list;
			}
			IntPtr vt = Marshal.ReadIntPtr(collection);
			CollectionGetCount getCount = (CollectionGetCount)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 3 * IntPtr.Size), typeof(CollectionGetCount));
			CollectionItem item = (CollectionItem)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 4 * IntPtr.Size), typeof(CollectionItem));
			hr = getCount(collection, out uint count);
			if (hr < 0)
			{
				return list;
			}
			for (uint i = 0; i < count; i++)
			{
				try
				{
					hr = item(collection, i, out IntPtr deviceRaw);
					if (hr < 0 || deviceRaw == IntPtr.Zero)
					{
						continue;
					}
					IMMDevice device = (IMMDevice)Marshal.GetObjectForIUnknown(deviceRaw);
					string id = null;
					try
					{
						device.GetId(out id);
					}
					catch
					{
					}
					string name = null;
					try
					{
						hr = device.OpenPropertyStore(0u, out IntPtr storeRaw);
						if (hr >= 0 && storeRaw != IntPtr.Zero)
						{
							IntPtr svt = Marshal.ReadIntPtr(storeRaw);
							StoreGetValue getValue = (StoreGetValue)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(svt, 5 * IntPtr.Size), typeof(StoreGetValue));
							name = ReadStoreString(getValue, storeRaw, PKEY_Device_FriendlyName);
							if (string.IsNullOrWhiteSpace(name))
							{
								name = ReadStoreString(getValue, storeRaw, PKEY_Device_DeviceDesc);
							}
						}
					}
					catch
					{
					}
					if (string.IsNullOrWhiteSpace(name))
					{
						name = "输出设备 " + (i + 1);
					}
					list.Add(new AudioDevice
					{
						Id = id,
						Name = name,
						IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
					});
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static string ReadStoreString(StoreGetValue getValue, IntPtr store, PROPERTYKEY key)
	{
		int hr = getValue(store, ref key, out PROPVARIANT value);
		if (hr < 0)
		{
			return null;
		}
		try
		{
			if (value.vt == 31)
			{
				return Marshal.PtrToStringUni(value.pointerValue);
			}
			return null;
		}
		finally
		{
			if (value.vt != 0)
			{
				PropVariantClear(ref value);
			}
		}
	}

	public static bool TrySetDefault(string id)
	{
		try
		{
			Guid clsid = CLSID_PolicyConfigClient;
			Guid iid = IID_IPolicyConfig;
			IPolicyConfig policyConfig = (IPolicyConfig)CreateInstance(ref clsid, ref iid);
			return policyConfig.SetDefaultEndpoint(id, ERole.Multimedia) >= 0;
		}
		catch
		{
			return false;
		}
	}
}
