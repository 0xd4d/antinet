// antinet test code.
// This is in the public domain

using System;
using antinet;

namespace antinet_test {
	public class AntiNetTest {
		const string sep = "\n\n\n";
		const string seperr = "********************************************************";
		public static int main(string[] args) {
			Console.WriteLine();
			Console.WriteLine("CLR: {0} - {1}", Environment.Version, IntPtr.Size == 4 ? "x86" : "x64");
			Console.WriteLine();

			Console.WriteLine("Press any key to install anti-managed debugger and anti-managed profiler code...");
			Console.ReadKey();
			Console.WriteLine(sep);

			// Here we go...
			if (AntiManagedDebugger.Initialize())
				Console.WriteLine("Anti-managed debugger code has been successfully initialized");
			else {
				Console.Error.WriteLine(seperr);
				Console.Error.WriteLine("FAILED TO INITIALIZE ANTI-DEBUGGER CODE");
				Console.Error.WriteLine(seperr);
			}

			Console.WriteLine("Try to attach a managed debugger/profiler or do it before this program starts.");
			Console.WriteLine("Check whether you can set breakpoints, step over code, etc... :)");
			Console.WriteLine("Press any key to exit...");
			Console.ReadKey();
			Console.WriteLine(sep);

			Console.WriteLine("Let's exit. Press any key (again!)...");
			Console.ReadKey();
			Console.WriteLine(sep);

			return 0;
		}
	}
}
