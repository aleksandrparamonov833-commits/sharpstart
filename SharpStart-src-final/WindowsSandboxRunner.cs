using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal sealed record CodeRunResult(
    bool Success,
    bool TimedOut,
    string Stdout,
    string Stderr,
    string Output,
    double ElapsedMilliseconds,
    string Error);

internal static class WindowsSandboxRunner
{
    private const int MaxCodeCharacters = 12_000;
    private const int MaxInputCharacters = 2_000;
    private const int MaxOutputCharacters = 8_000;
    private const uint WallTimeLimitMilliseconds = 60_000;
    private const ulong MemoryLimitBytes = 512UL * 1024 * 1024;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint JobObjectExtendedLimitInfoClass = 9;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitJobTime = 0x00000004;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly IntPtr ProcThreadAttributeSecurityCapabilities = new(0x00020009);

    public static async Task<CodeRunResult> RunAsync(string code, string input, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Безопасный локальный запуск кода доступен только в Windows AppContainer.");
        if (code.Length is 0 or > MaxCodeCharacters)
            throw new ArgumentException($"Код должен содержать от 1 до {MaxCodeCharacters} символов.", nameof(code));
        if (input.Length > MaxInputCharacters)
            throw new ArgumentException($"Ввод должен содержать не более {MaxInputCharacters} символов.", nameof(input));

        var dotnetPath = FindDotnetExecutable();
        var profileName = "SharpStart.Run." + Guid.NewGuid().ToString("N");
        IntPtr appContainerSid = IntPtr.Zero;
        IntPtr jobHandle = IntPtr.Zero;
        string? appContainerFolder = null;
        string? runFolder = null;
        var timer = Stopwatch.StartNew();

        try
        {
            appContainerSid = CreateProfileAndGetSid(profileName);
            appContainerFolder = GetAppContainerFolder(appContainerSid);
            var tempFolder = Path.Combine(appContainerFolder, "Temp");
            Directory.CreateDirectory(tempFolder);
            runFolder = Path.Combine(tempFolder, "sharpstart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runFolder);

            var projectFile = Path.Combine(runFolder, "Practice.csproj");
            await File.WriteAllTextAsync(projectFile, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Program.cs" />
                    <Compile Include="SandboxBootstrap.cs" />
                  </ItemGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runFolder, "Program.cs"), code, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runFolder, "stdin.txt"), input, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runFolder, "SandboxBootstrap.cs"), SandboxBootstrapSource, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runFolder, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>", cancellationToken);

            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) ThrowLastWin32Error("Не удалось создать ограничитель ресурсов для песочницы.");
            ConfigureJob(jobHandle);

            var buildLog = Path.Combine(runFolder, "build.log");
            var build = await RunSandboxProcessAsync(dotnetPath,
                new[] { "build", projectFile, "--nologo", "--disable-build-servers", "--verbosity", "quiet", $"-flp:LogFile={buildLog};Verbosity=quiet" },
                runFolder, appContainerFolder, appContainerSid, jobHandle, timer, cancellationToken);
            var buildOutput = await ReadBoundedFileAsync(buildLog, MaxOutputCharacters, cancellationToken);
            buildOutput = buildOutput.Replace(runFolder, "<песочница>", StringComparison.OrdinalIgnoreCase)
                .Replace(appContainerFolder, "<песочница>", StringComparison.OrdinalIgnoreCase);
            if (build.TimedOut)
                return new CodeRunResult(false, true, "", buildOutput, buildOutput,
                    Math.Round(timer.Elapsed.TotalMilliseconds, 1), "Превышен лимит времени на компиляцию и запуск (60 секунд).");
            if (build.ExitCode != 0)
                return new CodeRunResult(false, false, "", buildOutput, buildOutput,
                    Math.Round(timer.Elapsed.TotalMilliseconds, 1), "Исправь ошибки компиляции и попробуй ещё раз.");

            var assemblyPath = Path.Combine(runFolder, "bin", "Debug", "net10.0", "Practice.dll");
            var execution = await RunSandboxProcessAsync(dotnetPath, new[] { "exec", assemblyPath },
                runFolder, appContainerFolder, appContainerSid, jobHandle, timer, cancellationToken);
            var stdout = await ReadBoundedFileAsync(Path.Combine(runFolder, "stdout.txt"), MaxOutputCharacters, cancellationToken);
            var stderr = await ReadBoundedFileAsync(Path.Combine(runFolder, "stderr.txt"), MaxOutputCharacters, cancellationToken);
            var output = string.Join(Environment.NewLine, new[] { stdout.TrimEnd(), stderr.TrimEnd() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var success = !execution.TimedOut && execution.ExitCode == 0;
            return new CodeRunResult(success, execution.TimedOut, stdout, stderr, output,
                Math.Round(timer.Elapsed.TotalMilliseconds, 1),
                execution.TimedOut ? "Превышен лимит времени на компиляцию и запуск (60 секунд)." : success ? "" : "Исправь ошибки выполнения и попробуй ещё раз.");
        }
        finally
        {
            if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
            if (!string.IsNullOrWhiteSpace(runFolder))
            {
                try { Directory.Delete(runFolder, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (!string.IsNullOrWhiteSpace(profileName)) DeleteAppContainerProfile(profileName);
            if (appContainerSid != IntPtr.Zero) FreeSid(appContainerSid);
        }
    }

    private static async Task<(uint ExitCode, bool TimedOut)> RunSandboxProcessAsync(
        string dotnetPath, string[] arguments, string runFolder, string appContainerFolder,
        IntPtr appContainerSid, IntPtr jobHandle, Stopwatch timer, CancellationToken cancellationToken)
    {
        var remaining = Math.Max(1, WallTimeLimitMilliseconds - (uint)Math.Min(timer.ElapsedMilliseconds, WallTimeLimitMilliseconds));
        using var attributeList = new ProcThreadAttributeList(appContainerSid);
        var startupInfo = new StartupInfoEx();
        startupInfo.StartupInfo.Cb = (uint)Marshal.SizeOf<StartupInfoEx>();
        startupInfo.AttributeList = attributeList.Pointer;
        var environmentPointer = Marshal.StringToHGlobalUni(BuildEnvironment(appContainerFolder, dotnetPath));
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        var exited = false;
        try
        {
            var commandLine = new StringBuilder(string.Join(" ", new[] { dotnetPath }.Concat(arguments).Select(QuoteWindowsArgument)));
            if (!CreateProcessW(dotnetPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                    environmentPointer, runFolder, ref startupInfo, out var processInfo))
                ThrowLastWin32Error("Windows не смог запустить процесс внутри AppContainer.");
            processHandle = processInfo.Process;
            threadHandle = processInfo.Thread;
            if (!AssignProcessToJobObject(jobHandle, processHandle))
                ThrowLastWin32Error("Не удалось применить лимиты памяти и времени к песочнице.");
            if (ResumeThread(threadHandle) == uint.MaxValue)
                ThrowLastWin32Error("Не удалось запустить процесс в песочнице.");
            CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;

            var waitTask = Task.Run(() => WaitForSingleObject(processHandle, remaining), CancellationToken.None);
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken));
            if (completed != waitTask)
            {
                TerminateJobObject(jobHandle, 1);
                await Task.Run(() => WaitForSingleObject(processHandle, Infinite), CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                exited = true;
                return (1, true);
            }

            var waitResult = await waitTask;
            if (waitResult == WaitTimeout)
            {
                TerminateJobObject(jobHandle, 1);
                await Task.Run(() => WaitForSingleObject(processHandle, Infinite), CancellationToken.None);
                exited = true;
                return (1, true);
            }
            if (waitResult != WaitObject0) ThrowLastWin32Error("Не удалось получить результат процесса.");
            if (!GetExitCodeProcess(processHandle, out var exitCode)) ThrowLastWin32Error("Не удалось получить код завершения процесса.");
            exited = true;
            return (exitCode, false);
        }
        finally
        {
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero)
            {
                if (!exited) TerminateProcess(processHandle, 1);
                CloseHandle(processHandle);
            }
            Marshal.FreeHGlobal(environmentPointer);
        }
    }

    private static string FindDotnetExecutable()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            string.IsNullOrWhiteSpace(configuredRoot) ? null : Path.Combine(configuredRoot, "dotnet.exe"),
            Path.Combine(programFiles, "dotnet", "dotnet.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("Не найден .NET SDK. Установи .NET 10 SDK и перезапусти сайт.");
    }

    private static string BuildEnvironment(string appContainerFolder, string dotnetPath)
    {
        var dotnetRoot = Path.GetDirectoryName(dotnetPath)!;
        var temp = Path.Combine(appContainerFolder, "Temp");
        var local = Path.Combine(appContainerFolder, "Local");
        var roaming = Path.Combine(appContainerFolder, "Roaming");
        var cliHome = Path.Combine(appContainerFolder, "Dotnet");
        var packages = Path.Combine(appContainerFolder, "NuGet");
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(cliHome);
        Directory.CreateDirectory(packages);

        var entries = new List<string>();
        var parentEnvironment = GetEnvironmentStringsW();
        if (parentEnvironment == IntPtr.Zero) ThrowLastWin32Error("Не удалось получить структуру переменных процесса.");
        try
        {
            var cursor = parentEnvironment;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(cursor);
                if (string.IsNullOrEmpty(entry)) break;
                var separator = entry.IndexOf('=');
                if (separator == 0) separator = entry.IndexOf('=', 1);
                if (separator > 0)
                {
                    var name = entry[..separator];
                    var normalizedName = name.ToUpperInvariant();
                    var value = normalizedName switch
                    {
                        "APPDATA" => roaming,
                        "TEMP" or "TMP" => temp,
                        "USERPROFILE" => appContainerFolder,
                        _ => entry[(separator + 1)..]
                    };
                    if (IsSensitiveEnvironmentVariable(normalizedName)) value = "";
                    entries.Add(name + "=" + value);
                }
                cursor = IntPtr.Add(cursor, (entry.Length + 1) * sizeof(char));
            }
        }
        finally { FreeEnvironmentStringsW(parentEnvironment); }

        var block = string.Join('\0', entries) + "\0\0";
        return block;
    }

    private static bool IsSensitiveEnvironmentVariable(string name) =>
        name.StartsWith("CODEX_", StringComparison.Ordinal)
        || name.StartsWith("BROWSER_USE_", StringComparison.Ordinal)
        || name.StartsWith("NODE_REPL_", StringComparison.Ordinal)
        || name.StartsWith("AWS_", StringComparison.Ordinal)
        || name.StartsWith("AZURE_", StringComparison.Ordinal)
        || name.StartsWith("GITHUB_", StringComparison.Ordinal)
        || name.StartsWith("OPENAI_", StringComparison.Ordinal)
        || name.Contains("TOKEN", StringComparison.Ordinal)
        || name.Contains("SECRET", StringComparison.Ordinal)
        || name.Contains("PASSWORD", StringComparison.Ordinal)
        || name.Contains("CREDENTIAL", StringComparison.Ordinal)
        || name.Contains("AUTH", StringComparison.Ordinal)
        || name.Contains("SESSION", StringComparison.Ordinal)
        || name.Contains("COOKIE", StringComparison.Ordinal)
        || name.Contains("IDENTITY", StringComparison.Ordinal)
        || name.Contains("VERIFY", StringComparison.Ordinal);

    private static IntPtr CreateProfileAndGetSid(string profileName)
    {
        var result = CreateAppContainerProfile(profileName, "SharpStart C# runner",
            "Временный изолированный контейнер для запуска учебного кода.", IntPtr.Zero, 0, out var sid);
        if (result == 0) return sid;
        if (result != unchecked((int)0x800700B7)) Marshal.ThrowExceptionForHR(result);
        if (DeriveAppContainerSidFromAppContainerName(profileName, out sid) != 0 || sid == IntPtr.Zero)
            throw new InvalidOperationException("Не удалось получить идентификатор AppContainer.");
        return sid;
    }

    private static string GetAppContainerFolder(IntPtr sid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var sidText = new System.Security.Principal.SecurityIdentifier(sid).Value;
        var result = GetAppContainerFolderPath(sidText, out var pathPointer);
        if (result != 0 || pathPointer == IntPtr.Zero) Marshal.ThrowExceptionForHR(result);
        try
        {
            var path = Marshal.PtrToStringUni(pathPointer);
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Windows не вернул путь профиля AppContainer.");
            return path;
        }
        finally { CoTaskMemFree(pathPointer); }
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static void ConfigureJob(IntPtr jobHandle)
    {
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitActiveProcess | JobObjectLimitJobTime | JobObjectLimitJobMemory | JobObjectLimitKillOnJobClose,
                ActiveProcessLimit = 8,
                PerJobUserTimeLimit = 450_000_000
            },
            JobMemoryLimit = new UIntPtr(MemoryLimitBytes)
        };
        if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInfoClass, ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            ThrowLastWin32Error("Не удалось задать лимиты песочницы.");
    }

    private static async Task<string> ReadBoundedFileAsync(string path, int maxCharacters, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var builder = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            var remaining = maxCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
        }
        if (builder.Length == maxCharacters) builder.Append("\n… вывод обрезан …");
        return builder.ToString();
    }

    private const string SandboxBootstrapSource = """
        using System;
        using System.IO;
        using System.Runtime.CompilerServices;
        using System.Text;

        internal static class SandboxBootstrap
        {
            [ModuleInitializer]
            internal static void Initialize()
            {
                var folder = Environment.CurrentDirectory;
                Console.SetIn(new StreamReader(Path.Combine(folder, "stdin.txt"), new UTF8Encoding(false), true));
                Console.SetOut(CreateWriter(Path.Combine(folder, "stdout.txt")));
                Console.SetError(CreateWriter(Path.Combine(folder, "stderr.txt")));
            }

            private static TextWriter CreateWriter(string path) =>
                new BoundedWriter(new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true }, 8000);

            private sealed class BoundedWriter : TextWriter
            {
                private readonly TextWriter inner;
                private readonly int maximum;
                private int written;
                internal BoundedWriter(TextWriter inner, int maximum) { this.inner = inner; this.maximum = maximum; }
                public override Encoding Encoding => inner.Encoding;
                public override void Write(char value) { if (written++ < maximum) inner.Write(value); }
                public override void Write(string? value)
                {
                    if (value is null || written >= maximum) return;
                    var length = Math.Min(value.Length, maximum - written);
                    inner.Write(value.AsSpan(0, length));
                    written += length;
                }
                public override void Write(char[] buffer, int index, int count)
                {
                    if (written >= maximum) return;
                    var length = Math.Min(count, maximum - written);
                    inner.Write(buffer, index, length);
                    written += length;
                }
                public override void Flush() => inner.Flush();
                protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
            }
        }
        """;

    private static void ThrowLastWin32Error(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    private sealed class ProcThreadAttributeList : IDisposable
    {
        private readonly IntPtr list;
        private readonly IntPtr securityCapabilities;
        public IntPtr Pointer => list;

        public ProcThreadAttributeList(IntPtr appContainerSid)
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                ThrowLastWin32Error("Не удалось подготовить атрибуты песочницы.");
            securityCapabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities
            {
                AppContainerSid = appContainerSid,
                Capabilities = IntPtr.Zero,
                CapabilityCount = 0,
                Reserved = 0
            }, securityCapabilities, false);
            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeSecurityCapabilities,
                    securityCapabilities, new IntPtr(Marshal.SizeOf<SecurityCapabilities>()), IntPtr.Zero, IntPtr.Zero))
                ThrowLastWin32Error("Не удалось установить ограничения AppContainer.");
        }

        public void Dispose()
        {
            if (list != IntPtr.Zero) DeleteProcThreadAttributeList(list);
            if (securityCapabilities != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilities);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(string appContainerName, string displayName, string description,
        IntPtr capabilities, uint capabilityCount, out IntPtr appContainerSid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAppContainerFolderPath(string appContainerSid, out IntPtr path);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string appContainerName);
    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);
    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr threadHandle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetEnvironmentStringsW();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeEnvironmentStringsW(IntPtr environment);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, uint infoClass,
        ref JobObjectExtendedLimitInformation info, uint infoLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount,
        uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
}
