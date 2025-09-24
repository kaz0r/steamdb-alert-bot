using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SteamKit2;

class Program
{
    static SteamClient steamClient;
    static CallbackManager manager;
    static SteamUser steamUser;
    static SteamApps steamApps;

    static bool isRunning;
    static string user, pass;

    static void Main(string[] args)
    {
        // Load credentials from environment or prompt
        user = Environment.GetEnvironmentVariable("STEAM_USERNAME");
        pass = Environment.GetEnvironmentVariable("STEAM_PASSWORD");

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            // Load from .env file manually since .NET doesn't load it automatically
            if (File.Exists(".env"))
            {
                var envVars = File.ReadAllLines(".env");
                foreach (var line in envVars)
                {
                    if (line.StartsWith("STEAM_USERNAME="))
                        user = line.Substring("STEAM_USERNAME=".Length);
                    if (line.StartsWith("STEAM_PASSWORD="))
                        pass = line.Substring("STEAM_PASSWORD=".Length);
                }
            }

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                Console.Write("Username: ");
                user = Console.ReadLine();
                Console.Write("Password: ");
                pass = Console.ReadLine();
            }
        }

        // Create our steamclient instance
        steamClient = new SteamClient();

        // Create the callback manager which will route callbacks to function calls
        manager = new CallbackManager(steamClient);

        // Get the steamuser handler, which is used for logging on after successfully connecting
        steamUser = steamClient.GetHandler<SteamUser>();

        // Get the steam apps handler for app info
        steamApps = steamClient.GetHandler<SteamApps>();

        // Register a few callbacks we're interested in
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        manager.Subscribe<SteamApps.PICSProductInfoCallback>(OnProductInfo);

        isRunning = true;

        Console.WriteLine("Connecting to Steam...");

        // Connect to Steam
        steamClient.Connect();

        // Main loop
        while (isRunning)
        {
            // In order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }
    }

    static void OnConnected(SteamClient.ConnectedCallback callback)
    {
        Console.WriteLine("Connected to Steam! Logging in '{0}'...", user);

        steamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = user,
            Password = pass,
        });
    }

    static void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        Console.WriteLine("Disconnected from Steam");
        isRunning = false;
    }

    static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.WriteLine("Unable to logon to Steam: This account is SteamGuard protected.");
                isRunning = false;
                return;
            }

            Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);
            isRunning = false;
            return;
        }

        Console.WriteLine("Successfully logged on!");

        // Now request product info for Dota 2
        steamApps.PICSGetProductInfo(new SteamApps.PICSRequest(570), null);
    }

    static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        Console.WriteLine("Logged off of Steam: {0}", callback.Result);
    }

    static void OnProductInfo(SteamApps.PICSProductInfoCallback callback)
    {
        foreach (var app in callback.Apps.Values)
        {
            Console.WriteLine("Got product info for app {0}: {1}", app.ID, app.KeyValues["common"]["name"].Value);

            var depots = app.KeyValues["depots"];
            Console.WriteLine("Found {0} depot entries", depots.Children.Count);

            int contentDepots = 0;

            // Look for content depots
            foreach (var depot in depots.Children)
            {
                if (uint.TryParse(depot.Name, out uint depotId))
                {
                    var manifests = depot["manifests"];
                    if (manifests != null && manifests["public"] != null)
                    {
                        var publicManifest = manifests["public"];
                        var manifestId = publicManifest["gid"].AsUnsignedLong();
                        var size = publicManifest["size"].AsUnsignedLong();

                        Console.WriteLine("Depot {0}: Manifest {1}, Size: {2} bytes", depotId, manifestId, size);
                        contentDepots++;
                    }
                }
            }
        }

        Console.WriteLine("\nExiting...");
        steamUser.LogOff();
    }
}
