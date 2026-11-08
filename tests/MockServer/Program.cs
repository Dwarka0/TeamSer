// Simulates the process behaviour of ts3server.exe so process management can be tested
// without the proprietary TeamSpeak binary.
//
// Usage:
//   mock-server            run forever, exit cleanly on Ctrl+C (graceful)
//   mock-server --hang     ignore all stop signals, must be force-killed
//   mock-server --crash    exit(3) immediately after startup
//   mock-server --quick-exit exit(0) immediately
//   mock-server --fail <code> exit with a specific code

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MockServer;

public static class MockServer
{
    public static int Main(string[] args)
    {
        var flag = args.Length > 0 ? args[0] : "";

        // Marker file so tests can observe that this instance actually started.
        var dir = AppContext.BaseDirectory;
        File.WriteAllText(Path.Combine(dir, "mock.started"), $"{Environment.ProcessId}\n");

        switch (flag)
        {
            case "--crash":
                return 3;
            case "--quick-exit":
                return 0;
            case "--fail":
                return args.Length > 1 && int.TryParse(args[1], out var c) ? c : 1;
        }

        if (flag == "--hang")
        {
            // Deliberately unresponsive to Ctrl+C: test the force-kill path.
            Thread.Sleep(Timeout.Infinite);
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // emulate a clean SIGINT/Ctrl+C shutdown
            Console.WriteLine("graceful");
            e.Cancel = false;
            Environment.Exit(0);
        };

        // Optional: emulate a ServerQuery listener so readiness probing is exercised.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(dir, "mock.port"), port.ToString());

        var server = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!server.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(server.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                _ = Task.Run(() =>
                {
                    try
                    {
                        using (client)
                        {
                            var buf = new byte[1024];
                            _ = client.GetStream().ReadAsync(buf);
                            var banner = "Welcome to the TeamSpeak 3 ServerQuery"u8.ToArray();
                            client.GetStream().Write(banner);
                        }
                    }
                    catch (Exception) { /* client went away */ }
                });
            }
        });

        Console.CancelKeyPress += (_, _) => server.Cancel();

        // Parent watcher: if the parent app dies, this simulates nothing special.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Cancel();

        try { Thread.Sleep(Timeout.Infinite); }
        catch (ThreadInterruptedException) { }

        server.Cancel();
        return 0;
    }
}
