Code to prevent a managed .NET debugger/profiler from working
=============================================================

It uses undocumented features of Microsoft's CLR to prevent managed debuggers/profilers from working. It's possible that a future version of Microsoft's CLR will be updated so this code will either not be able to prevent the managed debugger/profiler from working or even cause unexpected behaviors.

Tested versions of the CLR:

* CLR 2.0.50727.42 (.NET 2.0 RTM) x86 and x64
* CLR 2.0.50727.5466 (latest version as of today) x86 and x64
* CLR 4.0.30319.1 (.NET 4.0 RTM) x86 and x64
* CLR 4.0.30319.17020 (.NET 4.5 Dev Preview) x86 and x64
* CLR 4.0.30319.17929 (.NET 4.5 RTM) x86 and x64

CLR 2.0 is used by .NET Framework 2.0 - 3.5. CLR 4.0 is used by .NET Framework 4.0 - 4.5.

Anti-managed debugger
=====================

Most anti-managed debugger code call `System.Diagnostics.Debugger.IsAttached` to check whether a managed debugger is present. This code doesn't do that. Instead, it prevents any managed .NET debugger from working by killing the .NET debugger thread. When this thread is killed, no managed .NET debugger can get any debug messages and will fail to work.

Note that it doesn't prevent non-managed debuggers from working (eg. `WinDbg` or `OllyDbg`). Non-managed debuggers can't debug managed code the way a managed debugger can. Debugging managed code using a non-managed debugger is not easy.

Technical details
-----------------

When the CLR starts, it creates a debugger class instance (called `Debugger`). This class will create a `DebuggerRCThread` instance which is the .NET debugger thread. This thread is only killed when the CLR exits. To exit this thread, one must clear its "keep-looping" instance field, and signal its event to wake it up.

Both of these instances are saved somewhere in the `.data` section.

In order to find the interesting `DebuggerRCThread` instance, we must scan the `.data` section for the `Debugger` instance pointer. The reason I chose to find this one first is that it contains the current `pid` which makes finding it a little easier. When we've found something that appears to be the `Debugger` instance and it has the `pid` in the correct location, we get the pointer to the `DebuggerRCThread` instance.

The `DebuggerRCThread` instance also has a pointer back to the `Debugger` instance. If it matches, then we can be very sure that we've found both of them.

Once we have the `DebuggerRCThread` instance, it's trivial to clear the keep-looping variable and signal the event so it wakes up and exits.

To prevent a debugger from attaching, one can clear the debugger IPC block's size field. If this is not an expected value, mscordbi will return an error and no debugger is able to attach.

Anti-managed profiler
=====================

A .NET profiler has access to a very powerful API that lets it trace and modify .NET assemblies at runtime. The profiler's DLL is loaded in the same process as the assembly or assemblies it's profiling. This anti-managed profiler code will prevent any attached profiler from receiving any profiler messages, and it will prevent any profiler from attaching. It won't kill any attached profiler, so if it detects a profiler you should exit the program.

Most code that checks for a running profiler checks the environment to see if any profiler environment variables are present. If they are, the program usually exits. This code doesn't check the environment, or registry, but instead checks the CLR's internal profiler status flags.

Technical details
-----------------
The CLR stores the profiler state in a variable in the `.data` section.

If it's CLR 2.0, we find this by looking for code that tests bits `[2:1]`. There are many such instructions in the code so it's easy to find. This global is called `g_profStatus`.

If it's CLR 4.0, the status is saved in `g_profControlBlock`. The profiler status is the third field in this structure (offset 08h (x86) or offset 0Ch (x64)). When this value is 0, no profiler is attached. Any other value indicates that a profiler is attaching, detaching or attached. We can find this status field by checking for code that compares the value to 4. There are plenty of instructions that check it for 4 so it's easy to find.

CLR 4.0 also allows profilers to attach at runtime. It creates a named event called "Global\CPFATE_PID_vCLRVERSION" where `PID` is the `pid` of the process the CLR is in and `CLRVERSION` is the first 3 version numbers (eg. `4.0.30319`). No extra thread is created to wait for this event to be signalled. Microsoft re-uses the Finalizer thread for this purpose. When attaching a profiler, the event is signalled by the profiler (in a different process). The CLR will now create a new thread that will create a named pipe, called "\\.\pipe\CPFATP_PID_vCLRVERSION". The profiler will open the pipe and send the attach message. Once the CLR gets the message, it will load the profiler DLL and the profiler is now attached and running in the CLR process.

To prevent profilers from attaching at runtime, one could create the named profiler event before the CLR has a chance to create it. Since this code is executed after the CLR has loaded, we can only hope to steal the named pipe instead. If we own the named pipe, no profiler will ever be able to attach. Most of the time, this is easy because the "attach profiler" thread isn't created yet, and since it's not created, the CLR hasn't created the named pipe.

It's possible to tell the CLR to always create the "attach profiler" thread if you set the `AttachThreadAlwaysOn` option (`COMPlus_AttachThreadAlwaysOn` environment variable). If this is enabled, the CLR will always start the thread, and this thread will never exit! If it never exits, then the CLR will always own the named pipe and a profiler will always be able to attach at runtime.

To solve this problem, one must find a few CLR global variables, patch them and then trigger an error so the thread will exit. That's what this code will do in the unlikely event that the CLR has created the named pipe. It will find the `ProfAPIMaxWaitForTriggerMs` option, set its default value to 0, and rename the variable so the user can't override its value (i.e., the default value is always used, which is 0). When it's 0, it will immediately return when waiting for client messages, and it will get a timeout error from Windows. When it receives an error at that point, it will exit the thread loop, but only if the `AttachThreadAlwaysOn` option isn't enabled. So this must be disabled as well. Finding it is a little trickier but there's a unique bit pattern that can be used to find it. Changing its value from 2 to 1 will make sure that the thread exits when it gets a timeout error. Now it's just a matter of waiting a few milliseconds and then create the named pipe.
