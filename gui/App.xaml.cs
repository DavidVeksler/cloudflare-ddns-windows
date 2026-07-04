using System.IO;
using System.Windows;
using System.Windows.Threading;
using CloudflareDdns.Gui.Services;

namespace CloudflareDdns.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless smoke test: parse the full XAML tree + construct the view model, write the
        // result to a file, and exit — used to validate the UI loads without a visible window.
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }

        // Headless one-shot: create an A record for every managed hostname that doesn't have
        // one yet, then exit. Writes to the real Cloudflare account — not a preview.
        if (e.Args.Any(a => a.Equals("--create-missing", StringComparison.OrdinalIgnoreCase)))
        {
            RunCreateMissing();
            return;
        }

        base.OnStartup(e);

        // Never let a stray background exception silently kill the control panel — surface it.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show(ex.ToString(), "Unexpected error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Something went wrong",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void RunSelfTest()
    {
        var outFile = Path.Combine(Path.GetTempPath(), "cfddns_selftest.txt");
        int code;
        string msg;
        try
        {
            var w = new MainWindow();   // parses every StaticResource/binding + builds MainViewModel
            _ = w.Title;                // touch it so the ctor isn't optimized away
            w.Close();

            // Exercise the exact Save() path (JsonObject.ToJsonString) against a throwaway dir,
            // since that codepath has previously broken silently at runtime despite compiling fine.
            var tempDir = Path.Combine(Path.GetTempPath(), "cfddns_selftest_cfg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var store = new LocalConfigStore(tempDir);
                store.Save(new ConfigModel { ApiToken = "test-token", Hostnames = { "a.example.com" } });
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }

            code = 0;
            msg = "SELFTEST_OK";
        }
        catch (Exception ex)
        {
            code = 3;
            msg = "SELFTEST_FAIL\n" + ex;
        }

        try { File.WriteAllText(outFile, msg); } catch { /* best effort */ }
        Shutdown(code);
    }

    private void RunCreateMissing()
    {
        var outFile = Path.Combine(Path.GetTempPath(), "cfddns_create_missing.txt");
        int code;
        string msg;
        try
        {
            var sink = new ObservableLogSink();
            var engine = new DdnsEngine(sink);
            // Run on a thread-pool thread: blocking .GetResult() on the WPF Dispatcher thread while
            // awaited continuations try to resume on that same (blocked) thread would deadlock.
            var results = Task.Run(() => engine.CreateMissingRecordsAsync(CancellationToken.None))
                .GetAwaiter().GetResult();

            var lines = results.Select(r => $"{(r.Created ? "CREATED" : "SKIPPED")}\t{r.Hostname}\t{r.Detail}");
            code = 0;
            msg = "CREATE_MISSING_OK\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            code = 3;
            msg = "CREATE_MISSING_FAIL\n" + ex;
        }

        try { File.WriteAllText(outFile, msg); } catch { /* best effort */ }
        Shutdown(code);
    }
}
