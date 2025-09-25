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
    static readonly object manifestCacheLock = new object();
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
                            // Try to get depot decryption key, but continue monitoring even if it fails
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Console.WriteLine("  🔑 Requesting app license and depot key for app {0}, depot {1}...", app.ID, depotId);

                                    // Try to request a free license for public apps
                                    try
                                    {
                                        Console.WriteLine("  📄 Attempting to acquire free license for app {0}...", app.ID);
                                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                        var licenseJob = steamApps.RequestFreeLicense(app.ID);
                                        var licenseTask = Task.Run(async () => await licenseJob, cts.Token);
                                        var licenseResult = await licenseTask;

                                        if (licenseResult.Result == EResult.OK)
                                        {
                                            Console.WriteLine("  ✅ Acquired free license for app {0}", app.ID);
                                        }
                                        else if (licenseResult.Result == EResult.AlreadyOwned)
                                        {
                                            Console.WriteLine("  ✅ App {0} already owned", app.ID);
                                        }
                                        else
                                        {
                                            Console.WriteLine("  ℹ️ Could not acquire free license for app {0}: {1}", app.ID, licenseResult.Result);
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        Console.WriteLine("  ⏰ License request timed out for app {0}", app.ID);
                                    }
                                    catch (Exception licenseEx)
                                    {
                                        Console.WriteLine("  ⚠️ License acquisition failed: {0}", licenseEx.Message);
                                    }

                                    // Get access token for the app
                                    var accessTokenJob = steamApps.PICSGetAccessTokens([app.ID], []);
                                    var accessTokenResult = await accessTokenJob;

                                    ulong accessToken = 0;

                                    if (accessTokenResult.AppTokens != null && accessTokenResult.AppTokens.TryGetValue(app.ID, out accessToken))
                                    {
                                        if (accessToken > 0)
                                        {
                                            Console.WriteLine("  ✅ Got access token for app {0}: {1}", app.ID, accessToken);
                                        }
                                        else
                                        {
                                            Console.WriteLine("  ℹ️ App {0} is public (no access token needed)", app.ID);
                                        }
                                    }
                                    else if (accessTokenResult.AppTokensDenied != null && accessTokenResult.AppTokensDenied.Contains(app.ID))
                                    {
                                        Console.WriteLine("  ❌ Access token denied for app {0} - using fallback monitoring", app.ID);
                                        await MonitorDepotWithoutKey(app.ID, appName, depotId, manifestId, size);
                                        return;
                                    }
                                    else
                                    {
                                        Console.WriteLine("  ⚠️ Failed to get access token - trying depot key anyway");
                                    }

                                    // Now request depot key with timeout
                                    using var keyRequestCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                    var depotKeyJob = steamApps.GetDepotDecryptionKey(depotId, app.ID);
                                    var depotKeyTask = Task.Run(async () => await depotKeyJob, keyRequestCts.Token);
                                    var depotKeyResult = await depotKeyTask;

                                    if (depotKeyResult.Result == EResult.OK)
                                    {
                                        Console.WriteLine("  ✅ Got depot key, scanning manifest...");
                                        await ScanManifestForSuspiciousFiles(app.ID, appName, depotId, manifestId, depotKeyResult.DepotKey);
                                    }
                                    else
                                    {
                                        Console.WriteLine("  ❌ Failed to get depot key: {0} - using fallback monitoring", depotKeyResult.Result);
                                        // Fallback: Track depot changes and analyze available metadata
                                        await MonitorDepotWithoutKey(app.ID, appName, depotId, manifestId, size);
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    Console.WriteLine("  ⏰ Depot key request timed out - using fallback monitoring");
                                    await MonitorDepotWithoutKey(app.ID, appName, depotId, manifestId, size);
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine("  ❌ Error getting depot key: {0} - using fallback monitoring", e.Message);
                                    if (e.InnerException != null)
                                    {
                                        Console.WriteLine("  🔍 Inner exception: {0}", e.InnerException.Message);
                                    }
                                    // Fallback: Track depot changes and analyze available metadata
                                    await MonitorDepotWithoutKey(app.ID, appName, depotId, manifestId, size);
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

            // Use a simple CDN server endpoint (implicit conversion from DnsEndPoint to Server)
            Server server = new System.Net.DnsEndPoint("steamcdn-a.akamaihd.net", 80);
            Console.WriteLine("  🌐 Using CDN server: steamcdn-a.akamaihd.net:80");

            // Download the actual manifest with depot key and timeout
            Console.WriteLine("  📥 Downloading manifest {0} for depot {1}...", manifestId, depotId);

            using var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var manifest = await cdnClient.DownloadManifestAsync(depotId, manifestId, manifestId, server, depotKey).WaitAsync(downloadCts.Token);

            if (manifest != null)
            {
                Console.WriteLine("  ✅ Manifest downloaded successfully, processing...");
                await ProcessManifest(manifest, appId, appName, depotId);
            }
            else
            {
                Console.WriteLine("  ❌ Manifest download returned null");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  ⏰ Manifest download timed out after 2 minutes");
        }
        catch (Exception e)
        {
            Console.WriteLine("  ❌ Error scanning manifest: {0}", e.Message);
            if (e.InnerException != null)
            {
                Console.WriteLine("  🔍 Inner exception: {0}", e.InnerException.Message);
            }
        }
    }

    static async Task ProcessManifest(DepotManifest manifest, uint appId, string appName, uint depotId)
    {
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
        HashSet<string> previousFiles;
        lock (manifestCacheLock)
        {
            previousFiles = previousManifests.TryGetValue(manifestKey, out var prev) ? prev : new HashSet<string>();
        }

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
        lock (manifestCacheLock)
        {
            previousManifests[manifestKey] = currentFiles;
        }
        SaveManifestCache();

        await Task.CompletedTask; // Satisfy async requirement
    }

    static async Task MonitorDepotWithoutKey(uint appId, string appName, uint depotId, ulong manifestId, ulong size)
    {
        try
        {
            Console.WriteLine("  📊 Monitoring depot {0} without key (manifest {1}, size: {2:N0} bytes)", depotId, manifestId, size);

            var manifestKey = $"{appId}_{depotId}";

            // Check if this is a new manifest (different from what we've seen before)
            var cacheKey = $"{manifestKey}_manifest";
            ulong previousManifestId;
            lock (manifestCacheLock)
            {
                previousManifestId = previousManifests.TryGetValue(cacheKey, out var prevSet) && prevSet.Count > 0
                    ? ulong.Parse(prevSet.First()) : 0UL;
            }

            if (previousManifestId != manifestId)
            {
                Console.WriteLine("  🔄 New manifest detected for depot {0}! (was: {1}, now: {2})", depotId, previousManifestId, manifestId);

                // Analyze suspicious patterns without downloading files
                var suspiciousIndicators = new List<string>();

                // Check for rapid size changes (could indicate file additions)
                var sizeCacheKey = $"{manifestKey}_size";
                HashSet<string>? prevSizeSet;
                lock (manifestCacheLock)
                {
                    previousManifests.TryGetValue(sizeCacheKey, out prevSizeSet);
                }

                if (prevSizeSet != null && prevSizeSet.Count > 0)
                {
                    if (ulong.TryParse(prevSizeSet.First(), out var previousSize))
                    {
                        var sizeDelta = size - previousSize;
                        var sizeChangePercent = previousSize > 0 ? (double)sizeDelta / previousSize * 100 : 0;

                        if (sizeDelta > 10_000_000) // 10MB+ increase
                        {
                            suspiciousIndicators.Add($"Large size increase: +{sizeDelta:N0} bytes (+{sizeChangePercent:F1}%)");
                        }
                        else if (sizeDelta > 0)
                        {
                            Console.WriteLine("  📈 Size increased by {0:N0} bytes ({1:F1}%)", sizeDelta, sizeChangePercent);
                        }
                    }
                }

                // Check app name for suspicious keywords
                var suspiciousNames = new[] { "crack", "keygen", "patch", "trainer", "cheat", "hack" };
                foreach (var keyword in suspiciousNames)
                {
                    if (appName.ToLowerInvariant().Contains(keyword))
                    {
                        suspiciousIndicators.Add($"Suspicious app name contains: '{keyword}'");
                        break;
                    }
                }

                // Generate alert for suspicious patterns
                if (suspiciousIndicators.Count > 0)
                {
                    Console.WriteLine("🚨 ALERT: App {0} ({1}) shows suspicious patterns!", appId, appName);
                    Console.WriteLine("  Depot {0} updated with potential indicators:", depotId);
                    foreach (var indicator in suspiciousIndicators)
                    {
                        Console.WriteLine("    • {0}", indicator);
                    }
                    Console.WriteLine("  ⚠️ Manual investigation recommended - cannot scan files without depot key");
                }
                else
                {
                    Console.WriteLine("  ✅ Depot updated but no suspicious patterns detected");
                }

                // Update cache with new manifest ID and size
                lock (manifestCacheLock)
                {
                    previousManifests[cacheKey] = [manifestId.ToString()];
                    previousManifests[sizeCacheKey] = [size.ToString()];
                }
                SaveManifestCache();
            }
            else
            {
                Console.WriteLine("  ℹ️ Same manifest as before, no changes detected");
            }

            await Task.Delay(100); // Small delay to avoid overwhelming the console
        }
        catch (Exception e)
        {
            Console.WriteLine("  ❌ Error in fallback monitoring: {0}", e.Message);
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
            Dictionary<string, List<string>> data;
            lock (manifestCacheLock)
            {
                data = previousManifests.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList()
                );
            }
            var json = JsonSerializer.Serialize(data, jsonOptions);
            File.WriteAllText(manifestCacheFile, json);
        }
        catch (Exception e)
        {
            Console.WriteLine("Warning: Could not save manifest cache: {0}", e.Message);
        }
    }
}
