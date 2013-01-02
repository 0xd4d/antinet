using System;

namespace antinet_test {
	public class AntiNetTest {
		public static int main(string[] args) {
			Console.WriteLine("CLR: {0} - {1}", Environment.Version, IntPtr.Size == 4 ? "x86" : "x64");
			Console.WriteLine("Press any key to exit...");
			Console.ReadKey();

			return 0;
		}
	}
}
