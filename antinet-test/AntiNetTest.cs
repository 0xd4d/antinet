// antinet test code.
// This is in the public domain

using System;
using System.Diagnostics;
using antinet;

namespace antinet_test {
	public class AntiNetTest {
		const string sep = "\n\n\n";
		const string seperr = "********************************************************";
		public static int main(string[] args) {
			Console.WriteLine();
			Console.WriteLine("CLR: {0} - {1}", Environment.Version, IntPtr.Size == 4 ? "x86" : "x64");
			Console.WriteLine();

			Console.WriteLine("Profiler attached: can't determine that yet");
			Console.WriteLine("Debugger.IsAttached: {0}", Debugger.IsAttached);
			Console.WriteLine("Press any key to install anti-managed debugger and anti-managed profiler code...");
			Console.ReadKey();
			Console.WriteLine(sep);

			if (AntiManagedProfiler.Initialize())
				Console.WriteLine("Anti-managed profiler code has been successfully initialized");
			else {
				Console.Error.WriteLine(seperr);
				Console.Error.WriteLine("FAILED TO INITIALIZE ANTI-PROFILER CODE");
				Console.Error.WriteLine(seperr);
			}
			if (AntiManagedDebugger.Initialize())
				Console.WriteLine("Anti-managed debugger code has been successfully initialized");
			else {
				Console.Error.WriteLine(seperr);
				Console.Error.WriteLine("FAILED TO INITIALIZE ANTI-DEBUGGER CODE");
				Console.Error.WriteLine(seperr);
			}
			Console.WriteLine();

			Console.WriteLine("Profiler attached: {0}", AntiManagedProfiler.IsProfilerAttached);
			if (AntiManagedProfiler.IsProfilerAttached) {
				Console.WriteLine("Preventing the profiler from receiving messages.");

				// NB: It's better to terminate the program since the profiler can still
				// call into the CLR and do stuff to your program.
				AntiManagedProfiler.PreventActiveProfilerFromReceivingProfilingMessages();
				Console.WriteLine("Prevented the profiler from receiving messages");
				Console.WriteLine("Profiler attached: {0} (at least that's what the CLR thinks)", AntiManagedProfiler.IsProfilerAttached);
			}
			Console.WriteLine("Debugger.IsAttached: {0}", Debugger.IsAttached);
			Console.WriteLine("Try to attach a debugger or profiler.");
			Console.WriteLine("Press any key to execute some code for the profiler...");
			Console.ReadKey();
			Console.WriteLine(sep);
			GenerateProfilerEvents();

			Console.WriteLine("Profiler attached: {0}", AntiManagedProfiler.IsProfilerAttached);
			Console.WriteLine("Debugger.IsAttached: {0}", Debugger.IsAttached);
			Console.WriteLine("Let's exit. Press any key (again!)...");
			Console.ReadKey();
			Console.WriteLine(sep);

			Console.WriteLine("Profiler attached: {0}", AntiManagedProfiler.IsProfilerAttached);
			Console.WriteLine("Debugger.IsAttached: {0}", Debugger.IsAttached);
			return 0;
		}

		static void GenerateProfilerEvents() {
			for (int i = 0; i < 10000; i++)
				CastIt(i);
		}

		static void CastIt(object o) {
			try {
				string s = (string)o;
			}
			catch (InvalidCastException) {
			}
		}
	}
}
