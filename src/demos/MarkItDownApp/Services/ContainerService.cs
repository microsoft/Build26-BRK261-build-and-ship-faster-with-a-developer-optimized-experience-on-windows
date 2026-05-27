using System.Diagnostics;
using System.Text;
using Microsoft.WSL.Containers;
using WslcProcess = Microsoft.WSL.Containers.Process;

namespace MarkItDownApp.Services;

/// <summary>
/// Manages the WSL Container session for the MarkItDown service
/// using the Microsoft.WSL.Containers C# SDK.
/// </summary>
public sealed class ContainerService
{
    public event Action<string>? OutputReceived;
    public event Action<string>? StatusChanged;

    private Session? _session;
    private Container? _container;
    private WslcProcess? _execProcess;

    private volatile bool _running;
    private volatile bool _shutdownCalled;

    private StreamWriter? _logFile;
    private readonly object _logLock = new();

    private const string SessionName = "MarkItDownService";
    private const string StoragePath = @"C:\WslcStorage\MarkItDown";
    private const string ImageName = "markitdown:latest";
    private const string ContainerNamePrefix = "markitdown";
    private const string DebugLogPath = @"C:\temp\MarkItDown\debug.log";

    private static readonly string ImageTarPath = Path.Combine(
        AppContext.BaseDirectory, "Container", "markitdown.tar");

    public ContainerService()
    {
        Directory.CreateDirectory(@"C:\temp\MarkItDown");

        try
        {
            _logFile = new StreamWriter(DebugLogPath, append: false) { AutoFlush = true };
        }
        catch { /* ignore */ }

        DebugLog("ContainerService created");
    }

    public bool IsRunning => _running;

    public void StartContainer()
    {
        if (_running) return;

        EmitStatus("Initializing WSL Container session...");

        Directory.CreateDirectory(@"C:\WslcStorage");
        Directory.CreateDirectory(StoragePath);
        Directory.CreateDirectory(@"C:\temp\MarkItDown");

        // --- 1. Session setup ---
        DebugLog("Step 1: Creating session settings...");
        var sessionSettings = new SessionSettings(SessionName, StoragePath)
        {
            CpuCount = 4,
            MemoryMB = 4096,
            VhdRequirements = new VhdOptions("default", 40UL * 1024 * 1024 * 1024, VhdType.Dynamic)
        };

        DebugLog("Step 2: Creating session...");
        _session = new Session(sessionSettings);
        _session.Start();
        DebugLog("Session created and started successfully");
        EmitStatus("Session created. Loading image...");

        // --- 2. Load image if not already in session ---
        DebugLog("Step 3: Checking for existing image...");
        bool imageFound = false;
        try
        {
            var images = _session.Images;
            foreach (var img in images)
            {
                DebugLog($"  Found image: {img.Name}");
                if (string.Equals(img.Name, ImageName, StringComparison.OrdinalIgnoreCase))
                {
                    imageFound = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to list images: {ex.Message}");
        }

        if (imageFound)
        {
            DebugLog("Image already loaded in session, skipping import");
        }
        else
        {
            DebugLog("Image not found, loading from tar file...");
            EmitStatus("Loading image (this may take a moment)...");
            try
            {
                var loadOp = _session.LoadImageAsync(ImageTarPath);
                loadOp.AsTask().GetAwaiter().GetResult();
                DebugLog("Image loaded successfully from tar");
            }
            catch (Exception ex)
            {
                DebugLog($"LoadImageAsync failed: {ex.Message}");
            }
        }
        EmitStatus("Image ready. Configuring container...");

        // --- 3. Container settings ---
        DebugLog("Step 4: Configuring container settings...");

        // Use sleep infinity as init process to keep container alive,
        // then exec the real workload separately (same pattern as herbert)
        var initProcess = new ProcessSettings { CmdLine = ["/bin/sleep", "infinity"] };

        var containerName = $"{ContainerNamePrefix}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var containerSettings = new ContainerSettings(ImageName)
        {
            Name = containerName,
            InitProcess = initProcess,
            NetworkingMode = ContainerNetworkingMode.Bridged,
            HostName = "markitdown",
            Flags = ContainerFlags.AutoRemove,
            PortMappings =
            [
                new ContainerPortMapping(8000, 8000, PortProtocol.TCP)
            ]
        };

        // --- 4. Create container ---
        DebugLog("Step 5: Creating container...");
        _container = _session.CreateContainer(containerSettings);
        DebugLog("Container created successfully");
        EmitStatus("Container created. Starting...");

        // --- 5. Start container ---
        DebugLog("Step 6: Starting container...");
        _container.Start();
        DebugLog("Container started successfully!");
        EmitStatus("Container running. Launching service...");

        // --- 6. Exec the uvicorn server inside the container ---
        DebugLog("Step 7: Launching exec process (uvicorn)...");
        var execSettings = new ProcessSettings
        {
            CmdLine = ["/opt/venv/bin/uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"],
            WorkingDirectory = "/app",
            OutputMode = ProcessOutputMode.Event
        };

        _execProcess = _container.CreateProcess(execSettings);

        _execProcess.OutputReceived += OnProcessOutput;
        _execProcess.ErrorReceived += OnProcessOutput;
        _execProcess.Exited += OnProcessExit;

        _execProcess.Start();

        _running = true;
        DebugLog("MarkItDown service launched successfully!");
        EmitStatus("Service is starting up...");
    }

    private void OnProcessOutput(byte[] data)
    {
        if (data.Length == 0) return;

        var text = Encoding.UTF8.GetString(data);
        DebugLog($"IO: {data.Length} bytes");
        EmitOutput(text);
    }

    private void OnProcessExit(int exitCode)
    {
        DebugLog($"Process exited with code {exitCode}");
        EmitStatus($"Process exited with code {exitCode}");
        _running = false;
    }

    public void Shutdown()
    {
        if (_shutdownCalled) return;
        _shutdownCalled = true;

        DebugLog("Shutdown started...");
        _running = false;

        if (_execProcess != null)
        {
            DebugLog("Signalling exec process...");
            try { _execProcess.Signal(Signal.SIGTERM); }
            catch { /* ignore */ }
            _execProcess = null;
        }

        if (_container != null)
        {
            DebugLog("Stopping container...");
            try { _container.Stop(Signal.SIGTERM, TimeSpan.FromSeconds(5)); }
            catch { /* ignore */ }

            DebugLog("Deleting container...");
            try { _container.Delete(DeleteContainerFlags.Force); }
            catch { /* ignore */ }
            _container = null;
        }

        if (_session != null)
        {
            DebugLog("Terminating session...");
            try { _session.Terminate(); }
            catch { /* ignore */ }
            _session = null;
        }

        DebugLog("Shutdown complete");

        lock (_logLock)
        {
            _logFile?.Dispose();
            _logFile = null;
        }
    }

    private void EmitOutput(string text)
    {
        OutputReceived?.Invoke(text);
    }

    private void EmitStatus(string text)
    {
        DebugLog($"STATUS: {text}");
        StatusChanged?.Invoke(text);
    }

    private void DebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{timestamp}] {message}";

        Debug.WriteLine($"[MarkItDown] {line}");

        lock (_logLock)
        {
            try { _logFile?.WriteLine(line); }
            catch { /* ignore */ }
        }
    }

}
