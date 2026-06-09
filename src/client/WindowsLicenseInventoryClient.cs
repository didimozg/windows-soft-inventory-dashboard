using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace WindowsLicenseInventory
{
    internal sealed class Program
    {
        private const string ServiceName = "WindowsLicenseInventory";
        internal const string ProductVersion = "1.3.0";

        private static int Main(string[] args)
        {
            ClientOptions options = ClientOptions.Parse(args);

            if (options.ShowVersion)
            {
                Console.WriteLine(ProductVersion);
                return 0;
            }

            if (options.RunOnce)
            {
                InventoryCollector collector = new InventoryCollector(options);
                collector.CollectAndSave();
                return 0;
            }

            ServiceBase.Run(new InventoryService(options));
            return 0;
        }

        private sealed class InventoryService : ServiceBase
        {
            private readonly ClientOptions options;
            private Timer timer;

            public InventoryService(ClientOptions options)
            {
                this.options = options;
                ServiceName = Program.ServiceName;
                CanStop = true;
                AutoLog = true;
            }

            protected override void OnStart(string[] args)
            {
                timer = new Timer(Collect, null, TimeSpan.Zero, TimeSpan.FromHours(options.IntervalHours));
            }

            protected override void OnStop()
            {
                if (timer != null)
                {
                    timer.Dispose();
                    timer = null;
                }
            }

            private void Collect(object state)
            {
                try
                {
                    InventoryCollector collector = new InventoryCollector(options);
                    collector.CollectAndSave();
                }
                catch
                {
                    // The service keeps running. Windows Event Log contains the service failure envelope.
                }
            }
        }
    }

    internal sealed class ClientOptions
    {
        public string ServerSharePath;
        public string ServerUrl;
        public string Token;
        public string OutputPath;
        public int IntervalHours;
        public bool RunOnce;
        public bool SkipSoftware;
        public bool ShowVersion;

        public static ClientOptions Parse(string[] args)
        {
            ClientOptions options = new ClientOptions();
            options.IntervalHours = 6;
            options.OutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsLicenseInventory");

            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i].ToLowerInvariant();
                if (key == "--once")
                {
                    options.RunOnce = true;
                }
                else if (key == "--version")
                {
                    options.ShowVersion = true;
                }
                else if (key == "--skip-software")
                {
                    options.SkipSoftware = true;
                }
                else if ((key == "--share" || key == "--server-share") && i + 1 < args.Length)
                {
                    options.ServerSharePath = args[++i];
                }
                else if (key == "--server-url" && i + 1 < args.Length)
                {
                    options.ServerUrl = args[++i];
                }
                else if (key == "--token" && i + 1 < args.Length)
                {
                    options.Token = args[++i];
                }
                else if (key == "--output" && i + 1 < args.Length)
                {
                    options.OutputPath = args[++i];
                }
                else if (key == "--interval-hours" && i + 1 < args.Length)
                {
                    int parsed;
                    if (Int32.TryParse(args[++i], out parsed) && parsed >= 1 && parsed <= 24)
                    {
                        options.IntervalHours = parsed;
                    }
                }
            }

            return options;
        }
    }

    internal sealed class InventoryCollector
    {
        private readonly ClientOptions options;

        public InventoryCollector(ClientOptions options)
        {
            this.options = options;
        }

        public void CollectAndSave()
        {
            Dictionary<string, object> inventory = Collect();
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(inventory);
            string fileName = SanitizeFileName(Environment.MachineName) + ".json";
            string localPath = options.OutputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? options.OutputPath
                : Path.Combine(options.OutputPath, fileName);

            WriteText(localPath, json);
            if (options.RunOnce)
            {
                Console.WriteLine("Local inventory file: " + localPath);
            }

            if (!String.IsNullOrEmpty(options.ServerSharePath))
            {
                string serverPath = Path.Combine(options.ServerSharePath, fileName);
                WriteText(serverPath, json);
                if (options.RunOnce)
                {
                    Console.WriteLine("Server share inventory file: " + serverPath);
                }
            }

            if (!String.IsNullOrEmpty(options.ServerUrl))
            {
                PostJson(options.ServerUrl, json, options.Token);
                if (options.RunOnce)
                {
                    Console.WriteLine("Inventory posted: " + options.ServerUrl);
                }
            }
        }

        private Dictionary<string, object> Collect()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            Dictionary<string, object> computer = QueryFirst("SELECT Domain, Manufacturer, Model FROM Win32_ComputerSystem");
            Dictionary<string, object> bios = QueryFirst("SELECT SerialNumber FROM Win32_BIOS");

            result["schemaVersion"] = "1.0";
            result["clientVersion"] = Program.ProductVersion;
            result["collectedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            result["computerName"] = Environment.MachineName;
            result["domain"] = GetString(computer, "Domain");
            result["ipAddresses"] = GetIpAddresses();
            result["manufacturer"] = GetString(computer, "Manufacturer");
            result["model"] = GetString(computer, "Model");
            result["serialNumber"] = GetString(bios, "SerialNumber");
            result["os"] = GetOperatingSystem();
            result["office"] = GetOfficeVersion();
            result["activation"] = GetActivation();
            result["software"] = options.SkipSoftware ? new ArrayList() : GetInstalledSoftware();

            return result;
        }

        private ArrayList GetIpAddresses()
        {
            ArrayList result = new ArrayList();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            ArrayList adapters = QueryList("SELECT IPAddress, IPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");

            foreach (Dictionary<string, object> adapter in adapters)
            {
                if (!adapter.ContainsKey("IPAddress") || adapter["IPAddress"] == null)
                {
                    continue;
                }

                IEnumerable addresses = adapter["IPAddress"] as IEnumerable;
                if (addresses == null || adapter["IPAddress"] is string)
                {
                    AddIpAddress(result, seen, Convert.ToString(adapter["IPAddress"]));
                    continue;
                }

                foreach (object address in addresses)
                {
                    AddIpAddress(result, seen, Convert.ToString(address));
                }
            }

            return result;
        }

        private static void AddIpAddress(ArrayList result, Dictionary<string, bool> seen, string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return;
            }

            IPAddress address;
            if (!IPAddress.TryParse(value, out address))
            {
                return;
            }

            if (IPAddress.IsLoopback(address))
            {
                return;
            }

            if (address.GetAddressBytes().Length != 4)
            {
                return;
            }

            string normalized = address.ToString();
            if (normalized.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (seen.ContainsKey(normalized))
            {
                return;
            }

            seen[normalized] = true;
            result.Add(normalized);
        }

        private Dictionary<string, object> GetOperatingSystem()
        {
            Dictionary<string, object> os = QueryFirst("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate FROM Win32_OperatingSystem");
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["caption"] = GetString(os, "Caption");
            result["version"] = GetString(os, "Version");
            result["buildNumber"] = GetString(os, "BuildNumber");
            result["architecture"] = GetString(os, "OSArchitecture");
            result["installDate"] = GetString(os, "InstallDate");
            return result;
        }

        private Dictionary<string, object> GetActivation()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["windows"] = GetActivationState(true);
            result["office"] = GetActivationState(false);
            return result;
        }

        private Dictionary<string, object> GetActivationState(bool windows)
        {
            string query = "SELECT Name, ApplicationID, LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL";
            ArrayList products = QueryList(query);
            Dictionary<string, object> result = new Dictionary<string, object>();

            foreach (Dictionary<string, object> product in products)
            {
                string name = GetString(product, "Name");
                string applicationId = GetString(product, "ApplicationID");
                bool match = windows
                    ? name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                    : applicationId.Equals("0ff1ce15-a989-479d-af46-f275c6370663", StringComparison.OrdinalIgnoreCase) ||
                      name.IndexOf("Office", StringComparison.OrdinalIgnoreCase) >= 0;

                if (match && Convert.ToInt32(product["LicenseStatus"]) == 1)
                {
                    result["activated"] = true;
                    result["product"] = name;
                    return result;
                }
            }

            result["activated"] = false;
            result["product"] = null;
            return result;
        }

        private Dictionary<string, object> GetOfficeVersion()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            string version = ReadRegistryString(Registry.LocalMachine, @"Software\Microsoft\Office\ClickToRun\Configuration", "VersionToReport");
            string products = ReadRegistryString(Registry.LocalMachine, @"Software\Microsoft\Office\ClickToRun\Configuration", "ProductReleaseIds");

            if (!String.IsNullOrEmpty(version) || !String.IsNullOrEmpty(products))
            {
                result["name"] = products;
                result["version"] = version;
                result["source"] = "ClickToRun";
                return result;
            }

            foreach (Dictionary<string, object> software in GetInstalledSoftware())
            {
                string name = GetString(software, "name");
                if (name.IndexOf("Microsoft Office", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Microsoft 365 Apps", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result["name"] = name;
                    result["version"] = GetString(software, "version");
                    result["source"] = "UninstallRegistry";
                    return result;
                }
            }

            result["name"] = null;
            result["version"] = null;
            result["source"] = null;
            return result;
        }

        private ArrayList GetInstalledSoftware()
        {
            ArrayList result = new ArrayList();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            ReadUninstallKey(result, seen, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            ReadUninstallKey(result, seen, Registry.LocalMachine, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
            return result;
        }

        private static void ReadUninstallKey(ArrayList result, Dictionary<string, bool> seen, RegistryKey root, string subKeyPath)
        {
            using (RegistryKey uninstall = root.OpenSubKey(subKeyPath))
            {
                if (uninstall == null)
                {
                    return;
                }

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using (RegistryKey item = uninstall.OpenSubKey(subKeyName))
                    {
                        if (item == null)
                        {
                            continue;
                        }

                        string displayName = Convert.ToString(item.GetValue("DisplayName", ""));
                        if (String.IsNullOrEmpty(displayName))
                        {
                            continue;
                        }

                        if (!IsVisibleSoftwareEntry(item))
                        {
                            continue;
                        }

                        string displayVersion = Convert.ToString(item.GetValue("DisplayVersion", ""));
                        string publisher = Convert.ToString(item.GetValue("Publisher", ""));
                        string installDate = Convert.ToString(item.GetValue("InstallDate", ""));
                        string key = (displayName + "|" + displayVersion + "|" + publisher).ToLowerInvariant();
                        if (seen.ContainsKey(key))
                        {
                            continue;
                        }
                        seen[key] = true;

                        Dictionary<string, object> software = new Dictionary<string, object>();
                        software["name"] = displayName;
                        software["version"] = displayVersion;
                        software["publisher"] = publisher;
                        software["installDate"] = installDate;
                        result.Add(software);
                    }
                }
            }
        }

        private static bool IsVisibleSoftwareEntry(RegistryKey item)
        {
            object systemComponent = item.GetValue("SystemComponent", 0);
            if (Convert.ToString(systemComponent) == "1")
            {
                return false;
            }

            string parentKeyName = Convert.ToString(item.GetValue("ParentKeyName", ""));
            if (!String.IsNullOrEmpty(parentKeyName))
            {
                return false;
            }

            string releaseType = Convert.ToString(item.GetValue("ReleaseType", ""));
            if (!String.IsNullOrEmpty(releaseType))
            {
                return false;
            }

            string uninstallString = Convert.ToString(item.GetValue("UninstallString", ""));
            string quietUninstallString = Convert.ToString(item.GetValue("QuietUninstallString", ""));
            if (String.IsNullOrEmpty(uninstallString) && String.IsNullOrEmpty(quietUninstallString))
            {
                return false;
            }

            return true;
        }

        private static string ReadRegistryString(RegistryKey root, string subKeyPath, string valueName)
        {
            using (RegistryKey key = root.OpenSubKey(subKeyPath))
            {
                if (key == null)
                {
                    return null;
                }

                return Convert.ToString(key.GetValue(valueName, null));
            }
        }

        private static Dictionary<string, object> QueryFirst(string query)
        {
            ArrayList list = QueryList(query);
            return list.Count > 0 ? (Dictionary<string, object>)list[0] : new Dictionary<string, object>();
        }

        private static ArrayList QueryList(string query)
        {
            ArrayList result = new ArrayList();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        foreach (PropertyData property in item.Properties)
                        {
                            row[property.Name] = property.Value;
                        }
                        result.Add(row);
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (!data.ContainsKey(key) || data[key] == null)
            {
                return null;
            }

            return Convert.ToString(data[key]);
        }

        private static string SanitizeFileName(string value)
        {
            StringBuilder builder = new StringBuilder();
            foreach (char c in value)
            {
                builder.Append(Char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            }
            return builder.ToString();
        }

        private static void WriteText(string path, string value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, value, new UTF8Encoding(false));
        }

        private static void PostJson(string url, string json, string token)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = body.Length;
            request.Timeout = 30000;

            if (!String.IsNullOrEmpty(token))
            {
                request.Headers["X-Inventory-Token"] = token;
            }

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(body, 0, body.Length);
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    throw new InvalidOperationException("Server returned HTTP " + (int)response.StatusCode);
                }
            }
        }
    }
}
