/*
 * This code was written by de4dot@gmail.com but is placed in the public domain.
 * Use at your own risk. You don't need to credit me, but it wouldn't hurt either. :)
 * 
 * It uses undocumented implementation features of Microsoft's .NET implementation
 * and could fail to work at any time. Most likely when you're sleeping.
 * 
 * Official site: https://bitbucket.org/0xd4d/antinet
 */

using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;

namespace antinet {
	/// <summary>
	/// This class will make sure that no managed .NET debugger can connect and
	/// debug this .NET process. This code assumes that it's Microsoft's .NET
	/// implementation (for the desktop) that is used. The only currently supported
	/// versions are .NET Framework 2.0 - 4.5 (CLR 2.0 and CLR 4.0).
	/// It prevents debugging by killing the .NET debugger thread. When it's killed,
	/// any debugger will fail to connect and any connected debugger will fail to
	/// receive any debugger messages.
	/// </summary>
	public static class AntiManagedDebugger {
		[DllImport("kernel32", CharSet = CharSet.Auto)]
		static extern IntPtr GetModuleHandle(string name);

		[DllImport("kernel32", CharSet = CharSet.Auto)]
		static extern uint GetCurrentProcessId();

		[DllImport("kernel32")]
		static extern bool SetEvent(IntPtr hEvent);

		class Info {
			/// <summary>
			/// Offset in <c>Debugger</c> of pointer to <c>DebuggerRCThread</c>.
			/// See Debugger::Startup() (after creating DebuggerRCThread).
			/// </summary>
			public int Debugger_pDebuggerRCThread;

			/// <summary>
			/// Offset in <c>Debugger</c> of the <c>pid</c>.
			/// See Debugger::Debugger().
			/// </summary>
			public int Debugger_pid;

			/// <summary>
			/// Offset in <c>DebuggerRCThread</c> of pointer to <c>Debugger</c>.
			/// See DebuggerRCThread::DebuggerRCThread().
			/// </summary>
			public int DebuggerRCThread_pDebugger;

			/// <summary>
			/// Offset in <c>DebuggerRCThread</c> of keep-looping boolean (1 byte).
			/// See Debugger::StopDebugger() or one of the first methods it calls.
			/// </summary>
			public int DebuggerRCThread_shouldKeepLooping;

			/// <summary>
			/// Offset in <c>DebuggerRCThread</c> of event to signal to wake it up.
			/// See Debugger::StopDebugger() or one of the first methods it calls.
			/// </summary>
			public int DebuggerRCThread_hEvent1;
		}

		/// <summary>
		/// CLR 2.0 x86 offsets
		/// </summary>
		static readonly Info info_CLR20_x86 = new Info {
			Debugger_pDebuggerRCThread = 4,
			Debugger_pid = 8,
			DebuggerRCThread_pDebugger = 0x30,
			DebuggerRCThread_shouldKeepLooping = 0x3C,
			DebuggerRCThread_hEvent1 = 0x40,
		};

		/// <summary>
		/// CLR 2.0 x64 offsets
		/// </summary>
		static readonly Info info_CLR20_x64 = new Info {
			Debugger_pDebuggerRCThread = 8,
			Debugger_pid = 0x10,
			DebuggerRCThread_pDebugger = 0x58,
			DebuggerRCThread_shouldKeepLooping = 0x70,
			DebuggerRCThread_hEvent1 = 0x78,
		};

		/// <summary>
		/// CLR 4.0 x86 offsets
		/// </summary>
		static readonly Info info_CLR40_x86_1 = new Info {
			Debugger_pDebuggerRCThread = 8,
			Debugger_pid = 0xC,
			DebuggerRCThread_pDebugger = 0x34,
			DebuggerRCThread_shouldKeepLooping = 0x40,
			DebuggerRCThread_hEvent1 = 0x44,
		};

		/// <summary>
		/// CLR 4.0 x86 offsets (rev >= 17379 (.NET 4.5 Beta, but not .NET 4.5 Dev Preview))
		/// </summary>
		static readonly Info info_CLR40_x86_2 = new Info {
			Debugger_pDebuggerRCThread = 8,
			Debugger_pid = 0xC,
			DebuggerRCThread_pDebugger = 0x30,
			DebuggerRCThread_shouldKeepLooping = 0x3C,
			DebuggerRCThread_hEvent1 = 0x40,
		};

		/// <summary>
		/// CLR 4.0 x64 offsets (this is the same in all CLR 4.0 versions, even in .NET 4.5 RTM)
		/// </summary>
		static readonly Info info_CLR40_x64 = new Info {
			Debugger_pDebuggerRCThread = 0x10,
			Debugger_pid = 0x18,
			DebuggerRCThread_pDebugger = 0x58,
			DebuggerRCThread_shouldKeepLooping = 0x70,
			DebuggerRCThread_hEvent1 = 0x78,
		};

		/// <summary>
		/// Must be called to initialize anti-managed debugger code
		/// </summary>
		public unsafe static bool Initialize() {
			var info = GetInfo();
			var pDebuggerRCThread = FindDebuggerRCThreadAddress(info);
			if (pDebuggerRCThread == IntPtr.Zero)
				return false;

			// Let's do this
			*((byte*)pDebuggerRCThread + info.DebuggerRCThread_shouldKeepLooping) = 0;
			IntPtr hEvent = *(IntPtr*)((byte*)pDebuggerRCThread + info.DebuggerRCThread_hEvent1);
			bool b = SetEvent(hEvent);
			return true;
		}

		/// <summary>
		/// Returns the correct <see cref="Info"/> instance
		/// </summary>
		static Info GetInfo() {
			switch (Environment.Version.Major) {
			case 2: return IntPtr.Size == 4 ? info_CLR20_x86 : info_CLR20_x64;
			case 4:
				if (Environment.Version.Revision <= 17020)
					return IntPtr.Size == 4 ? info_CLR40_x86_1 : info_CLR40_x64;
				return IntPtr.Size == 4 ? info_CLR40_x86_2 : info_CLR40_x64;
			default: goto case 4;	// Assume CLR 4.0
			}
		}

		/// <summary>
		/// Tries to find the address of the <c>DebuggerRCThread</c> instance in memory
		/// </summary>
		/// <param name="info">The info we need</param>
		[HandleProcessCorruptedStateExceptions, SecurityCritical]	// Req'd on .NET 4.0
		static unsafe IntPtr FindDebuggerRCThreadAddress(Info info) {
			uint pid = GetCurrentProcessId();

			try {
				IntPtr clrAddr = GetCLR();
				if (clrAddr == IntPtr.Zero)
					return IntPtr.Zero;
				var peInfo = new PEInfo(clrAddr);

				IntPtr sectionAddr;
				uint sectionSize;
				if (!peInfo.FindSection(".data", out sectionAddr, out sectionSize))
					return IntPtr.Zero;

				// Try to find the Debugger instance location in the data section
				byte* p = (byte*)sectionAddr;
				byte* end = (byte*)sectionAddr + sectionSize;
				for (; p + IntPtr.Size <= end; p += IntPtr.Size) {
					IntPtr pDebugger = *(IntPtr*)p;
					if (pDebugger == IntPtr.Zero)
						continue;

					try {
						// All allocations are pointer-size aligned
						if (!IsAlignedPointer(pDebugger))
							continue;

						// Make sure pid is correct
						uint pid2 = *(uint*)((byte*)pDebugger + info.Debugger_pid);
						if (pid != pid2)
							continue;

						IntPtr pDebuggerRCThread = *(IntPtr*)((byte*)pDebugger + info.Debugger_pDebuggerRCThread);

						// All allocations are pointer-size aligned
						if (!IsAlignedPointer(pDebuggerRCThread))
							continue;

						// Make sure it points back to Debugger
						IntPtr pDebugger2 = *(IntPtr*)((byte*)pDebuggerRCThread + info.DebuggerRCThread_pDebugger);
						if (pDebugger != pDebugger2)
							continue;

						return pDebuggerRCThread;
					}
					catch (SEHException) {
					}
					catch (AccessViolationException) {
					}
					catch (NullReferenceException) {
					}
				}
			}
			catch (SEHException) {
			}
			catch (AccessViolationException) {
			}
			catch (NullReferenceException) {
			}

			return IntPtr.Zero;
		}

		static bool IsAlignedPointer(IntPtr addr) {
			return (addr.ToInt64() & (IntPtr.Size - 1)) == 0;
		}

		static IntPtr GetCLR() {
			if (Environment.Version.Major == 2)
				return GetModuleHandle("mscorwks");
			return GetModuleHandle("clr");
		}
	}
}
