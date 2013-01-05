/*
 * This code was written by de4dot@gmail.com but is placed in the public domain.
 * Use at your own risk. You don't need to credit me, but it wouldn't hurt either. :)
 * 
 * It uses undocumented features of Microsoft's CLR to prevent managed profilers
 * from working and could fail to work at any time. Most likely when you're sleeping.
 * 
 * Official site: https://bitbucket.org/0xd4d/antinet
 */

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace antinet {
	/// <summary>
	/// This class will make sure that no managed .NET profiler is working.
	/// </summary>
	/// <remarks>
	/// <para>
	/// To detect profilers that are loaded when the CLR is loaded, this code will find
	/// the CLR profiler status flag in the data section. If CLR 4.0 is used, the code
	/// will find instructions in clr.dll that compares a dword location with the value 4.
	/// 4 is the value that is stored when a profiler has successfully attached to the
	/// CLR. If CLR 2.0 is used, then it will look for code that tests bits 1 and 2 of
	/// some dword location.
	/// </para>
	/// <para>
	/// CLR 4.0 allows a profiler to attach at any time. For this to work, it will create
	/// a named event, called "Global\CPFATE_PID_vCLRVERSION" where PID is the pid
	/// of the process the CLR is in and CLRVERSION is the first 3 version numbers
	/// (eg. 4.0.30319). It's actually the Finalizer thread that waits on this event. :)
	/// </para>
	/// <para>
	/// When a profiler tries to attach, it will try to connect to a named pipe. This pipe's
	/// name is called "\\.\pipe\CPFATP_PID_vCLRVERSION". It will then signal the above event to
	/// wake up the Finalizer thread. If the event can't be created, then no profiler can ever
	/// attach. Any code that runs before the CLR has a chance to "steal" this event from it
	/// to prevent the CLR from allowing profilers to attach at runtime. We can't do it. But
	/// we can create the named pipe. If we own the named pipe, then no profiler can ever send
	/// the attach message and they'll never be able to attach.
	/// </para>
	/// <para>
	/// Most of the time, the named pipe isn't created. All we do is create the named pipe
	/// and we've prevented profilers from attaching at runtime. If the pipe has already been
	/// created, we must make sure the CLR closes the pipe and exits the "profiler attacher"
	/// thread. By default, it will wait up to 5 mins (300,000ms) before exiting the wait loop.
	/// You can change this value with the ProfAPIMaxWaitForTriggerMs option (dword in registry)
	/// or COMPlus_ProfAPIMaxWaitForTriggerMs environment value. If the AttachThreadAlwaysOn
	/// option (COMPlus_AttachThreadAlwaysOn env value) is enabled, the attach thread will
	/// never exit and the named pipe is never closed.
	/// </para>
	/// </remarks>
	public static class AntiManagedProfiler {
		static IProfilerDetector profilerDetector;

		interface IProfilerDetector {
			bool IsProfilerAttached { get; }
			bool WasProfilerAttached { get; }
			bool Initialize();
			void PreventActiveProfilerFromReceivingProfilingMessages();
		}

		class ProfilerDetectorCLR20 : IProfilerDetector {
			/// <summary>
			/// Address of CLR 2.0's profiler status flag. If one or both of bits 1 or 2 is set,
			/// a profiler is attached.
			/// </summary>
			IntPtr profilerStatusFlag;

			bool wasAttached;

			public bool IsProfilerAttached {
				get {
					unsafe {
						if (profilerStatusFlag == IntPtr.Zero)
							return false;
						return (*(uint*)profilerStatusFlag & 6) != 0;
					}
				}
			}

			public bool WasProfilerAttached {
				get { return wasAttached; }
			}

			public bool Initialize() {
				bool result = FindProfilerStatus();
				wasAttached = IsProfilerAttached;
				return result;
			}

			/// <summary>
			/// This code tries to find the CLR 2.0 profiler status flag. It searches the whole
			/// .text section for a certain instruction.
			/// </summary>
			/// <returns><c>true</c> if it was found, <c>false</c> otherwise</returns>
			unsafe bool FindProfilerStatus() {
				// Record each hit here and pick the one with the most hits
				var addrCounts = new Dictionary<IntPtr, int>();
				try {
					var peInfo = PEInfo.GetCLR();
					if (peInfo == null)
						return false;

					IntPtr sectionAddr;
					uint sectionSize;
					if (!peInfo.FindSection(".text", out sectionAddr, out sectionSize))
						return false;

					const int MAX_COUNTS = 50;
					byte* p = (byte*)sectionAddr;
					byte* end = (byte*)sectionAddr + sectionSize;
					for (; p < end; p++) {
						IntPtr addr;

						// F6 05 XX XX XX XX 06	test byte ptr [mem],6
						if (*p == 0xF6 && p[1] == 0x05 && p[6] == 0x06) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 2));
							else
								addr = new IntPtr((void*)(p + 7 + *(int*)(p + 2)));
						}
						else
							continue;

						if (!PEInfo.IsAligned(addr, 4))
							continue;
						if (!peInfo.IsValidImageAddress(addr, 4))
							continue;

						try {
							*(uint*)addr = *(uint*)addr;
						}
						catch {
							continue;
						}

						int count = 0;
						addrCounts.TryGetValue(addr, out count);
						count++;
						addrCounts[addr] = count;
						if (count >= MAX_COUNTS)
							break;
					}
				}
				catch {
				}
				var foundAddr = GetMax(addrCounts, 5);
				if (foundAddr == IntPtr.Zero)
					return false;

				profilerStatusFlag = foundAddr;
				return true;
			}

			public unsafe void PreventActiveProfilerFromReceivingProfilingMessages() {
				if (profilerStatusFlag == IntPtr.Zero)
					return;
				*(uint*)profilerStatusFlag &= ~6U;
			}
		}

		class ProfilerDetectorCLR40 : IProfilerDetector {
			[DllImport("kernel32", CharSet = CharSet.Auto)]
			static extern uint GetCurrentProcessId();

			[DllImport("kernel32", SetLastError = true)]
			static extern SafeFileHandle CreateNamedPipe(string lpName, uint dwOpenMode,
			   uint dwPipeMode, uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize,
			   uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

			/// <summary>
			/// Address of the profiler control block. Only some fields are interesting and
			/// here they are in order:
			/// 
			/// <code>
			/// EEToProfInterfaceImpl*
			/// uint profilerEventMask
			/// uint profilerStatus
			/// </code>
			/// 
			/// <c>profilerStatus</c> is <c>0</c> when no profiler is attacheded. Any other value
			/// indicates that a profiler is attached, attaching, or detaching. It's <c>4</c>
			/// when a profiler is attached. When it's attached, it will receive messages from
			/// the CLR.
			/// </summary>
			IntPtr profilerControlBlock;

			SafeFileHandle profilerPipe;

			bool wasAttached;

			public bool IsProfilerAttached {
				get {
					unsafe {
						if (profilerControlBlock == IntPtr.Zero)
							return false;
						return *(uint*)((byte*)profilerControlBlock + IntPtr.Size + 4) != 0;
					}
				}
			}

			public bool WasProfilerAttached {
				get { return wasAttached; }
			}

			public bool Initialize() {
				bool result = FindProfilerControlBlock();
				TakeOwnershipOfNamedPipe();
				wasAttached = IsProfilerAttached;
				return result;
			}

			void TakeOwnershipOfNamedPipe() {
				string pipeName = string.Format(@"\\.\pipe\CPFATP_{0}_v{1}.{2}.{3}",
							GetCurrentProcessId(), Environment.Version.Major,
							Environment.Version.Minor, Environment.Version.Build);
				profilerPipe = CreateNamedPipe(pipeName,
											0x40000003,	// FILE_FLAG_OVERLAPPED | PIPE_ACCESS_DUPLEX
											6,			// PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE
											1,			// nMaxInstances
											0x24,		// nOutBufferSize
											0x338,		// nInBufferSize
											1000,		// nDefaultTimeOut
											IntPtr.Zero);	// lpSecurityAttributes

				if (profilerPipe.IsInvalid) {
					// The CLR has already created the named pipe. Either the
					// AttachThreadAlwaysOn CLR option is enabled or some profiler has just
					// attached or is attaching.

					//TODO:
				}
			}

			/// <summary>
			/// This code tries to find the CLR 4.0 profiler control block address. It does this
			/// by searching for the code that accesses the profiler status field.
			/// </summary>
			/// <returns><c>true</c> if it was found, <c>false</c> otherwise</returns>
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			unsafe bool FindProfilerControlBlock() {
				// Record each hit here and pick the one with the most hits
				var addrCounts = new Dictionary<IntPtr, int>();
				try {
					var peInfo = PEInfo.GetCLR();
					if (peInfo == null)
						return false;

					IntPtr sectionAddr;
					uint sectionSize;
					if (!peInfo.FindSection(".text", out sectionAddr, out sectionSize))
						return false;

					const int MAX_COUNTS = 50;
					byte* p = (byte*)sectionAddr;
					byte* end = (byte*)sectionAddr + sectionSize;
					for (; p < end; p++) {
						IntPtr addr;

						// A1 xx xx xx xx		mov eax,[mem]
						// 83 F8 04				cmp eax,4
						if (*p == 0xA1 && p[5] == 0x83 && p[6] == 0xF8 && p[7] == 0x04) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 1));
							else
								addr = new IntPtr((void*)(p + 5 + *(int*)(p + 1)));
						}
						// 8B 05 xx xx xx xx	mov eax,[mem]
						// 83 F8 04				cmp eax,4
						else if (*p == 0x8B && p[1] == 0x05 && p[6] == 0x83 && p[7] == 0xF8 && p[8] == 0x04) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 2));
							else
								addr = new IntPtr((void*)(p + 6 + *(int*)(p + 2)));
						}
						// 83 3D XX XX XX XX 04	cmp dword ptr [mem],4
						else if (*p == 0x83 && p[1] == 0x3D && p[6] == 0x04) {
							if (IntPtr.Size == 4)
								addr = new IntPtr((void*)*(uint*)(p + 2));
							else
								addr = new IntPtr((void*)(p + 7 + *(int*)(p + 2)));
						}
						else
							continue;

						if (!PEInfo.IsAligned(addr, 4))
							continue;
						if (!peInfo.IsValidImageAddress(addr, 4))
							continue;

						// Valid values are 0-4. 4 being attached.
						try {
							if (*(uint*)addr > 4)
								continue;
							*(uint*)addr = *(uint*)addr;
						}
						catch {
							continue;
						}

						int count = 0;
						addrCounts.TryGetValue(addr, out count);
						count++;
						addrCounts[addr] = count;
						if (count >= MAX_COUNTS)
							break;
					}
				}
				catch {
				}
				var foundAddr = GetMax(addrCounts, 5);
				if (foundAddr == IntPtr.Zero)
					return false;

				profilerControlBlock = new IntPtr((byte*)foundAddr - (IntPtr.Size + 4));
				return true;
			}

			public unsafe void PreventActiveProfilerFromReceivingProfilingMessages() {
				if (profilerControlBlock == IntPtr.Zero)
					return;
				*(uint*)((byte*)profilerControlBlock + IntPtr.Size + 4) = 0;
			}
		}

		/// <summary>
		/// Returns <c>true</c> if a profiler was attached, is attaching or detaching.
		/// </summary>
		public static bool IsProfilerAttached {
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			get {
				try {
					if (profilerDetector == null)
						return false;
					return profilerDetector.IsProfilerAttached;
				}
				catch {
				}
				return false;
			}
		}

		/// <summary>
		/// Returns <c>true</c> if a profiler was attached, is attaching or detaching.
		/// </summary>
		public static bool WasProfilerAttached {
			[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
			get {
				try {
					if (profilerDetector == null)
						return false;
					return profilerDetector.WasProfilerAttached;
				}
				catch {
				}
				return false;
			}
		}

		/// <summary>
		/// Must be called to initialize anti-managed profiler code. This method should only
		/// be called once per process. I.e., don't call it from every loaded .NET DLL.
		/// </summary>
		/// <returns><c>true</c> if successful, <c>false</c> otherwise</returns>
		public static bool Initialize() {
			profilerDetector = CreateProfilerDetector();
			return profilerDetector.Initialize();
		}

		static IProfilerDetector CreateProfilerDetector() {
			if (Environment.Version.Major == 2)
				return new ProfilerDetectorCLR20();
			return new ProfilerDetectorCLR40();
		}

		/// <summary>
		/// Prevents any active profiler from receiving any profiling messages. Since the
		/// profiler is still in memory, it can call into the CLR even if it doesn't receive
		/// any messages. It's better to terminate the application than call this method.
		/// </summary>
		public static void PreventActiveProfilerFromReceivingProfilingMessages() {
			if (profilerDetector == null)
				return;
			profilerDetector.PreventActiveProfilerFromReceivingProfilingMessages();
		}

		static IntPtr GetMax(Dictionary<IntPtr, int> addresses, int minCount) {
			IntPtr foundAddr = IntPtr.Zero;
			int maxCount = 0;

			foreach (var kv in addresses) {
				if (foundAddr == IntPtr.Zero || maxCount < kv.Value) {
					foundAddr = kv.Key;
					maxCount = kv.Value;
				}
			}

			return maxCount >= minCount ? foundAddr : IntPtr.Zero;
		}
	}
}
