// Services/WindowsPowerService.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public class WindowsPowerService : IPowerService, IDisposable
{
    private const string DialogProcessName = "ShutdownDialog";

    // Win32Shutdown flags: 1 = Shutdown, 2 = Reboot, 4 = Forced (закрыть приложения без вопросов)
    private const int ForcedShutdown = 1 | 4;
    private const int ForcedReboot = 2 | 4;

    private readonly ILogger<WindowsPowerService> _logger;
    private readonly PowerScheduler _scheduler;

    // Windows API imports
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr Token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const uint GENERIC_ALL_ACCESS = 0x10000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int NoActiveSession = -1; // WTSGetActiveConsoleSessionId возвращает 0xFFFFFFFF

    public WindowsPowerService(ILogger<WindowsPowerService> logger)
    {
        _logger = logger;
        _scheduler = new PowerScheduler(logger);
    }

    public Task<PowerResponse> ShutdownAsync(int delay = 0)
        => Task.FromResult(Execute(PowerAction.Shutdown, delay));

    public Task<PowerResponse> RebootAsync(int delay = 0)
        => Task.FromResult(Execute(PowerAction.Reboot, delay));

    public Task<PowerResponse> ShutdownWithDialogAsync(int delay = 30, string message = "Компьютер будет выключен")
        => Task.FromResult(StartDialog(PowerAction.Shutdown, delay, message));

    public Task<PowerResponse> RebootWithDialogAsync(int delay = 30, string message = "Компьютер будет перезагружен")
        => Task.FromResult(StartDialog(PowerAction.Reboot, delay, message));

    public Task<PowerResponse> CancelShutdownAsync()
    {
        var cancelled = _scheduler.Cancel();
        var dialogsClosed = KillDialogs();

        // На случай выключения, запущенного через shutdown.exe /t вручную или диалогом
        try
        {
            using var abort = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/a",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            abort?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "shutdown /a failed");
        }

        var somethingCancelled = cancelled || dialogsClosed > 0;
        _logger.LogInformation("Cancel requested: scheduled={Scheduled}, dialogs closed={Dialogs}", cancelled, dialogsClosed);

        return Task.FromResult(new PowerResponse
        {
            Success = true,
            Message = somethingCancelled ? "Shutdown/reboot cancelled" : "Nothing to cancel"
        });
    }

    public PendingPowerAction? GetPending() => _scheduler.Pending;

    private PowerResponse Execute(PowerAction action, int delay)
    {
        var flags = action == PowerAction.Reboot ? ForcedReboot : ForcedShutdown;

        if (delay > 0)
            return _scheduler.Schedule(action, delay, () => ExecuteWin32Shutdown(action, flags));

        _scheduler.Cancel();
        return ExecuteWin32Shutdown(action, flags);
    }

    private PowerResponse StartDialog(PowerAction action, int delay, string message)
    {
        try
        {
            var dialogPath = Path.Combine(AppContext.BaseDirectory, DialogProcessName + ".exe");

            if (!File.Exists(dialogPath))
            {
                _logger.LogWarning("ShutdownDialog.exe not found at {Path}", dialogPath);
                return new PowerResponse { Success = false, Error = "ShutdownDialog.exe not found" };
            }

            // Кавычки в сообщении сломали бы разбор аргументов командной строки
            var safeMessage = message.Replace("\"", "'");
            var mode = action == PowerAction.Reboot ? "reboot" : "shutdown";

            if (!StartProcessInUserSession(dialogPath, $"{delay} \"{safeMessage}\" {mode}"))
            {
                return new PowerResponse { Success = false, Error = "Failed to start dialog in user session (no user logged in?)" };
            }

            _logger.LogInformation("{Action} with dialog scheduled in {Delay}s", action, delay);
            return new PowerResponse { Success = true, Message = $"{action} scheduled in {delay}s with dialog" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Action} dialog", action);
            return new PowerResponse { Success = false, Error = ex.Message };
        }
    }

    private int KillDialogs()
    {
        var killed = 0;
        foreach (var process in Process.GetProcessesByName(DialogProcessName))
        {
            try
            {
                process.Kill();
                killed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close ShutdownDialog (pid {Pid})", process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }
        return killed;
    }

    private PowerResponse ExecuteWin32Shutdown(PowerAction action, int flags)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate
            });
            scope.Connect();

            var query = new ObjectQuery("SELECT * FROM Win32_OperatingSystem WHERE Primary=true");
            using var searcher = new ManagementObjectSearcher(scope, query);

            foreach (ManagementObject os in searcher.Get())
            {
                var inParams = os.GetMethodParameters("Win32Shutdown");
                inParams["Flags"] = flags;
                inParams["Reserved"] = 0;

                var result = os.InvokeMethod("Win32Shutdown", inParams, null);
                var returnValue = Convert.ToUInt32(result?["ReturnValue"] ?? uint.MaxValue);

                _logger.LogInformation("{Action} initiated with result: {Result}", action, returnValue);

                return returnValue == 0
                    ? new PowerResponse { Success = true, Message = $"{action} initiated" }
                    : new PowerResponse { Success = false, Error = $"Win32Shutdown returned {returnValue}" };
            }

            return new PowerResponse { Success = false, Error = "No Win32_OperatingSystem found" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Win32Shutdown failed");
            return new PowerResponse { Success = false, Error = ex.Message };
        }
    }

    private bool StartProcessInUserSession(string applicationPath, string arguments)
    {
        int sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoActiveSession)
        {
            _logger.LogWarning("No active console session found");
            return false;
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr duplicateToken = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                _logger.LogError("WTSQueryUserToken failed: {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            if (!DuplicateTokenEx(userToken, GENERIC_ALL_ACCESS, IntPtr.Zero, 2, 1, out duplicateToken))
            {
                _logger.LogError("DuplicateTokenEx failed: {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            STARTUPINFO si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
                dwFlags = 0x00000001, // STARTF_USESHOWWINDOW
                wShowWindow = 5       // SW_SHOW
            };

            string commandLine = $"\"{applicationPath}\" {arguments}";

            bool result = CreateProcessAsUser(
                duplicateToken, null, commandLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, null,
                ref si, out PROCESS_INFORMATION pi);

            if (result)
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                _logger.LogInformation("Process started in user session {SessionId}", sessionId);
            }
            else
            {
                _logger.LogError("CreateProcessAsUser failed: {Error}", Marshal.GetLastWin32Error());
            }

            return result;
        }
        finally
        {
            if (userToken != IntPtr.Zero)
                CloseHandle(userToken);
            if (duplicateToken != IntPtr.Zero)
                CloseHandle(duplicateToken);
        }
    }

    public void Dispose() => _scheduler.Dispose();
}
