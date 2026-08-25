using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace InnerTune;

public partial class App : System.Windows.Application
{
    private const int MaxActivationBytes = 32_768;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _activationCancel;

    protected override void OnStartup(StartupEventArgs e)
    {
        WindowsPathEnvironment.RefreshCurrentProcess();
        if (!AppRuntime.HasSafeTestConfiguration)
        {
            Shutdown(2);
            return;
        }

        var activation = e.Args.FirstOrDefault(argument =>
            PlaylistShareCodec.IsPlaylistLink(argument) ||
            (AppRuntime.IsTestMode && argument.Equals(AppRuntime.TestShareActivation, StringComparison.Ordinal))) ?? "show";
        var instanceName = $"InnerTune.Windows.Singleton.{AppRuntime.InstanceKey}";
        _mutex = new Mutex(true, instanceName, out var isFirst);
        _ownsMutex = isFirst;
        if (!isFirst)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", $"InnerTune.Activate.{AppRuntime.InstanceKey}", PipeDirection.Out);
                client.Connect(800);
                var bytes = Encoding.UTF8.GetBytes(activation);
                if (bytes.Length <= MaxActivationBytes) client.Write(bytes);
            }
            catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        MainWindow = window;
        if (AppRuntime.IsTestMode)
        {
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
        }
        window.Show();
        if (activation != "show") window.HandleActivation(activation);
        _activationCancel = new CancellationTokenSource();
        _ = ListenForActivationAsync(window, _activationCancel.Token);
    }

    private static async Task ListenForActivationAsync(MainWindow window, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream($"InnerTune.Activate.{AppRuntime.InstanceKey}", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var message = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = await server.ReadAsync(buffer, token)) > 0)
                {
                    if (message.Length + read > MaxActivationBytes) throw new InvalidDataException("Activation message is too large.");
                    message.Write(buffer, 0, read);
                }
                var activation = Encoding.UTF8.GetString(message.ToArray());
                await window.Dispatcher.InvokeAsync(() => window.HandleActivation(activation));
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(250, token); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancel?.Cancel();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
