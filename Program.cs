using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;
using System.Text.Json;

class Program
{
    static SteamClient steamClient = null!;
    static CallbackManager manager = null!;
    static SteamUser steamUser = null!;
    static SteamApps steamApps = null!;

    static bool isRunning;
    static string user = string.Empty, pass = string.Empty;
    static uint lastChangeNumber = 0;
    static Dictionary<string, HashSet<string>> previousManifests = new Dictionary<string, HashSet<string>>();
    static readonly string manifestCacheFile = "manifest_cache.json";
    static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions { WriteIndented = true };

    static void Main(string[] args)
    {
        // Load previous manifest cache
        LoadManifestCache();

        // Load credentials from environment or prompt
        user = Environment.GetEnvironmentVariable("STEAM_USERNAME") ?? string.Empty;
        pass = Environment.GetEnvironmentVariable("STEAM_PASSWORD") ?? string.Empty;

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
                user = Console.ReadLine() ?? string.Empty;
                Console.Write("Password: ");
                pass = Console.ReadLine() ?? string.Empty;
            }
        }

        // Create our steamclient instance
        steamClient = new SteamClient();

        // Create the callback manager which will route callbacks to function calls
        manager = new CallbackManager(steamClient);

        // Get the steamuser handler, which is used for logging on after successfully connecting
        steamUser = steamClient.GetHandler<SteamUser>()!;

        // Get the steam apps handler for app info
        steamApps = steamClient.GetHandler<SteamApps>()!;

        // Register a few callbacks we're interested in
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        manager.Subscribe<SteamApps.PICSProductInfoCallback>(OnProductInfo);
        manager.Subscribe<SteamApps.PICSChangesCallback>(OnPICSChanges);

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

        // Start monitoring changes
        Console.WriteLine("Starting change monitoring...");
        MonitorChanges();
    }

    static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        Console.WriteLine("Logged off of Steam: {0}", callback.Result);
    }

    static void OnProductInfo(SteamApps.PICSProductInfoCallback callback)
    {
        foreach (var app in callback.Apps.Values)
        {
            var appName = app.KeyValues["common"]["name"].Value ?? "Unknown";
            Console.WriteLine("📦 App updated: {0} - {1}", app.ID, appName);

            var depots = app.KeyValues["depots"];
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

                        Console.WriteLine("  Depot {0}: Manifest {1}, Size: {2:N0} bytes", depotId, manifestId, size);
                        contentDepots++;

                        // Scan for suspicious files (only scan first depot to avoid spam)
                        if (contentDepots == 1 && manifestId > 0)
                        {
                            // Get depot decryption key first
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Console.WriteLine("  🔑 Requesting depot key for depot {0}...", depotId);
                                    var depotKeyResult = await steamApps.GetDepotDecryptionKey(depotId, app.ID);

                                    if (depotKeyResult.Result == EResult.OK)
                                    {
                                        Console.WriteLine("  ✅ Got depot key, scanning manifest...");
                                        await ScanManifestForSuspiciousFiles(app.ID, appName, depotId, manifestId, depotKeyResult.DepotKey);
                                    }
                                    else
                                    {
                                        Console.WriteLine("  ❌ Failed to get depot key: {0}", depotKeyResult.Result);
                                    }
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine("  ❌ Error getting depot key: {0}", e.Message);
                                }
                            });
                        }
                    }
                }
            }

            Console.WriteLine("  Total depots: {0}", contentDepots);
        }
    }

    static void MonitorChanges()
    {
        // Get recent changes
        steamApps.PICSGetChangesSince(lastChangeNumber, true, false);
    }

    static void OnPICSChanges(SteamApps.PICSChangesCallback callback)
    {
        Console.WriteLine("Got PICS changes! Current change number: {0}", callback.CurrentChangeNumber);

        if (callback.AppChanges.Count > 0)
        {
            Console.WriteLine("App changes detected: {0}", callback.AppChanges.Count);

            foreach (var appChange in callback.AppChanges.Take(10)) // Show first 10
            {
                Console.WriteLine("App {0} changed", appChange.Key);

                // Get product info for changed app
                steamApps.PICSGetProductInfo(new SteamApps.PICSRequest(appChange.Key), null);
            }
        }

        // Update our change number
        lastChangeNumber = callback.CurrentChangeNumber;

        // Schedule next check in 30 seconds
        var timer = new Timer(_ => MonitorChanges(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(-1));
    }

    static async Task ScanManifestForSuspiciousFiles(uint appId, string appName, uint depotId, ulong manifestId, byte[] depotKey)
    {
        try
        {
            Console.WriteLine("🔍 Scanning manifest for depot {0}...", depotId);

            // Create CDN client
            var cdnClient = new Client(steamClient);

            // Use Steam's CDN servers (create server using implicit conversion from DNS endpoint)
            SteamKit2.CDN.Server server = new System.Net.DnsEndPoint("steamcdn-a.akamaihd.net", 80);

            // Download the actual manifest with depot key
            Console.WriteLine("  📥 Downloading manifest {0} for depot {1}...", manifestId, depotId);
            var manifest = await cdnClient.DownloadManifestAsync(depotId, manifestId, 0, server, depotKey);

            if (manifest?.Files == null)
            {
                Console.WriteLine("  ❌ Failed to download or parse manifest");
                return;
            }

            Console.WriteLine("  📄 Scanning {0} files in manifest...", manifest.Files.Count);

            // Get manifest key for caching
            var manifestKey = $"{appId}_{depotId}";

            // Get current file list
            var currentFiles = new HashSet<string>(manifest.Files.Select(f => f.FileName));

            // Get previous file list
            var previousFiles = previousManifests.TryGetValue(manifestKey, out var prev) ? prev : [];

            // Find new files (files in current but not in previous)
            var newFiles = currentFiles.Except(previousFiles).ToList();

            Console.WriteLine("  📊 New files: {0}, Total files: {1}", newFiles.Count, currentFiles.Count);

            // Look for suspicious file extensions in new files only (prioritizing .7z, .zip, .bat as requested)
            var suspiciousExtensions = new[] { ".7z", ".zip", ".bat", ".exe", ".cmd", ".ps1", ".vbs", ".jar" };
            var suspiciousNewFiles = new List<string>();

            foreach (var fileName in newFiles)
            {
                var lowerFileName = fileName.ToLowerInvariant();

                foreach (var ext in suspiciousExtensions)
                {
                    if (lowerFileName.EndsWith(ext))
                    {
                        suspiciousNewFiles.Add(fileName);
                        break;
                    }
                }
            }

            if (suspiciousNewFiles.Count > 0)
            {
                Console.WriteLine("🚨 ALERT: App {0} ({1}) has {2} NEW suspicious files!", appId, appName, suspiciousNewFiles.Count);
                Console.WriteLine("  New suspicious files:");
                foreach (var file in suspiciousNewFiles.Take(10)) // Show first 10
                {
                    Console.WriteLine("    {0}", file);
                }
                if (suspiciousNewFiles.Count > 10)
                {
                    Console.WriteLine("    ... and {0} more new suspicious files", suspiciousNewFiles.Count - 10);
                }
            }
            else if (newFiles.Count > 0)
            {
                Console.WriteLine("  ✅ {0} new files found, but none suspicious", newFiles.Count);
            }
            else
            {
                Console.WriteLine("  ℹ️ No new files detected");
            }

            // Update cache with current file list
            previousManifests[manifestKey] = currentFiles;
            SaveManifestCache();
        }
        catch (Exception e)
        {
            Console.WriteLine("  ❌ Error scanning manifest: {0}", e.Message);
        }
    }

    static void LoadManifestCache()
    {
        try
        {
            if (File.Exists(manifestCacheFile))
            {
                var json = File.ReadAllText(manifestCacheFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                previousManifests = data?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new HashSet<string>(kvp.Value)
                ) ?? new Dictionary<string, HashSet<string>>();
                Console.WriteLine("Loaded manifest cache with {0} entries", previousManifests.Count);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Warning: Could not load manifest cache: {0}", e.Message);
        }
    }

    static void SaveManifestCache()
    {
        try
        {
            var data = previousManifests.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList()
            );
            var json = JsonSerializer.Serialize(data, jsonOptions);
            File.WriteAllText(manifestCacheFile, json);
        }
        catch (Exception e)
        {
            Console.WriteLine("Warning: Could not save manifest cache: {0}", e.Message);
        }
    }
}
