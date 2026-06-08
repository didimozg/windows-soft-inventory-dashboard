using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WindowsLicenseInventory
{
    internal sealed class Program
    {
        private const string ServiceName = "WindowsLicenseInventoryServer";
        internal const string ProductVersion = "1.4.1";

        private static int Main(string[] args)
        {
            ServerOptions options = ServerOptions.Parse(args);

            if (options.ShowVersion)
            {
                Console.WriteLine(ProductVersion);
                return 0;
            }

            if (options.ConsoleMode)
            {
                InventoryServer server = new InventoryServer(options);
                server.Start();
                Console.WriteLine("Server URL: http://localhost:" + options.Port + "/");
                Console.WriteLine("Press Enter to stop.");
                Console.ReadLine();
                server.Stop();
                return 0;
            }

            ServiceBase.Run(new InventoryServerService(options));
            return 0;
        }

        private sealed class InventoryServerService : ServiceBase
        {
            private readonly InventoryServer server;

            public InventoryServerService(ServerOptions options)
            {
                ServiceName = Program.ServiceName;
                CanStop = true;
                AutoLog = true;
                server = new InventoryServer(options);
            }

            protected override void OnStart(string[] args)
            {
                server.Start();
            }

            protected override void OnStop()
            {
                server.Stop();
            }
        }
    }

    internal sealed class ServerOptions
    {
        public int Port;
        public IPAddress Address;
        public string DataPath;
        public string ContentPath;
        public string Token;
        public string WebUsername;
        public string WebPassword;
        public bool ConsoleMode;
        public bool ShowVersion;

        public static ServerOptions Parse(string[] args)
        {
            ServerOptions options = new ServerOptions();
            options.Port = 8080;
            options.Address = IPAddress.Any;
            options.DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"WindowsLicenseInventory\server");
            options.ContentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"WindowsLicenseInventory\server-content");

            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i].ToLowerInvariant();
                if (key == "--console")
                {
                    options.ConsoleMode = true;
                }
                else if (key == "--version")
                {
                    options.ShowVersion = true;
                }
                else if ((key == "--port" || key == "--listen-port") && i + 1 < args.Length)
                {
                    Int32.TryParse(args[++i], out options.Port);
                }
                else if (key == "--bind" && i + 1 < args.Length)
                {
                    IPAddress parsed;
                    if (IPAddress.TryParse(args[++i], out parsed))
                    {
                        options.Address = parsed;
                    }
                }
                else if (key == "--prefix" && i + 1 < args.Length)
                {
                    string prefix = args[++i].Replace("+", "localhost");
                    Uri uri;
                    if (Uri.TryCreate(prefix, UriKind.Absolute, out uri) && uri.Port > 0)
                    {
                        options.Port = uri.Port;
                    }
                }
                else if (key == "--data" && i + 1 < args.Length)
                {
                    options.DataPath = args[++i];
                }
                else if (key == "--content" && i + 1 < args.Length)
                {
                    options.ContentPath = args[++i];
                }
                else if (key == "--token" && i + 1 < args.Length)
                {
                    options.Token = args[++i];
                }
                else if (key == "--web-username" && i + 1 < args.Length)
                {
                    options.WebUsername = args[++i];
                }
                else if (key == "--web-password" && i + 1 < args.Length)
                {
                    options.WebPassword = args[++i];
                }
            }

            return options;
        }
    }

    internal sealed class InventoryServer
    {
        private readonly ServerOptions options;
        private TcpListener listener;
        private Thread worker;
        private bool running;

        public InventoryServer(ServerOptions options)
        {
            this.options = options;
        }

        public void Start()
        {
            if (!Directory.Exists(options.DataPath))
            {
                Directory.CreateDirectory(options.DataPath);
            }

            listener = new TcpListener(options.Address, options.Port);
            listener.Start();
            running = true;
            worker = new Thread(ListenLoop);
            worker.IsBackground = true;
            worker.Start();
        }

        public void Stop()
        {
            running = false;
            if (listener != null)
            {
                listener.Stop();
            }
        }

        private void ListenLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch
                {
                    if (running)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
        }

        private void HandleClient(object state)
        {
            using (TcpClient client = (TcpClient)state)
            {
                NetworkStream stream = client.GetStream();
                RequestContext request = ReadRequest(stream);
                try
                {
                    if (request.Method == "POST" && request.Path == "/api/v1/inventory")
                    {
                        ReceiveInventory(stream, request);
                    }
                    else if (!IsWebRequestAuthorized(request))
                    {
                        SendUnauthorized(stream);
                    }
                    else if (request.Method == "GET" && request.Path == "/api/v1/clients")
                    {
                        SendJson(stream, BuildClientIndex());
                    }
                    else if (request.Method == "DELETE" && request.Path.StartsWith("/api/v1/clients/", StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteClient(stream, request);
                    }
                    else if (request.Method == "GET" && (request.Path == "/" || request.Path == "/index.html"))
                    {
                        SendDashboardFile(stream, "index.html", DashboardHtml, "text/html; charset=utf-8");
                    }
                    else if (request.Method == "GET" && request.Path == "/app.js")
                    {
                        SendDashboardFile(stream, "app.js", DashboardJs, "application/javascript; charset=utf-8");
                    }
                    else if (request.Method == "GET" && request.Path == "/styles.css")
                    {
                        SendDashboardFile(stream, "styles.css", DashboardCss, "text/css; charset=utf-8");
                    }
                    else
                    {
                        SendText(stream, "Not found", "text/plain; charset=utf-8", 404);
                    }
                }
                catch (Exception ex)
                {
                    SendText(stream, ex.Message, "text/plain; charset=utf-8", 500);
                }
            }
        }

        private void ReceiveInventory(NetworkStream stream, RequestContext request)
        {
            string token = request.Headers.ContainsKey("x-inventory-token") ? request.Headers["x-inventory-token"] : null;
            if (!String.IsNullOrEmpty(options.Token) && token != options.Token)
            {
                SendText(stream, "Unauthorized", "text/plain; charset=utf-8", 401);
                return;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> inventory = serializer.Deserialize<Dictionary<string, object>>(request.Body);
            string computerName = Convert.ToString(inventory.ContainsKey("computerName") ? inventory["computerName"] : "unknown");
            string path = Path.Combine(options.DataPath, SanitizeFileName(computerName) + ".json");
            File.WriteAllText(path, request.Body, new UTF8Encoding(false));
            SendJson(stream, "{\"status\":\"ok\"}");
        }

        private void DeleteClient(NetworkStream stream, RequestContext request)
        {
            const string prefix = "/api/v1/clients/";
            string rawComputerName = request.Path.Substring(prefix.Length);
            int queryStart = rawComputerName.IndexOf('?');
            if (queryStart >= 0)
            {
                rawComputerName = rawComputerName.Substring(0, queryStart);
            }

            string computerName = Uri.UnescapeDataString(rawComputerName).Trim();
            if (String.IsNullOrEmpty(computerName))
            {
                SendText(stream, "{\"error\":\"computer name is required\"}", "application/json; charset=utf-8", 400);
                return;
            }

            string fileName = SanitizeFileName(computerName) + ".json";
            string path = Path.Combine(options.DataPath, fileName);
            if (!File.Exists(path))
            {
                SendText(stream, "{\"error\":\"client not found\"}", "application/json; charset=utf-8", 404);
                return;
            }

            File.Delete(path);
            SendJson(stream, "{\"status\":\"deleted\"}");
        }

        private bool IsWebRequestAuthorized(RequestContext request)
        {
            if (String.IsNullOrEmpty(options.WebUsername) && String.IsNullOrEmpty(options.WebPassword))
            {
                return true;
            }

            string authorization = request.Headers.ContainsKey("authorization") ? request.Headers["authorization"] : null;
            if (String.IsNullOrEmpty(authorization) || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                string encoded = authorization.Substring(6).Trim();
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                int separator = decoded.IndexOf(':');
                if (separator < 0)
                {
                    return false;
                }

                string username = decoded.Substring(0, separator);
                string password = decoded.Substring(separator + 1);
                return username == options.WebUsername && password == options.WebPassword;
            }
            catch
            {
                return false;
            }
        }

        private void SendDashboardFile(NetworkStream stream, string fileName, string fallback, string contentType)
        {
            string path = Path.Combine(options.ContentPath, fileName);
            if (File.Exists(path))
            {
                SendText(stream, File.ReadAllText(path, Encoding.UTF8), contentType, 200);
                return;
            }

            SendText(stream, fallback, contentType, 200);
        }

        private string BuildClientIndex()
        {
            ArrayList clients = new ArrayList();
            JavaScriptSerializer serializer = new JavaScriptSerializer();

            foreach (string file in Directory.GetFiles(options.DataPath, "*.json"))
            {
                try
                {
                    string raw = File.ReadAllText(file, Encoding.UTF8);
                    Dictionary<string, object> client = serializer.Deserialize<Dictionary<string, object>>(raw);
                    client["sourceFile"] = Path.GetFileName(file);
                    client["sourceUpdatedAt"] = File.GetLastWriteTimeUtc(file).ToString("yyyy-MM-ddTHH:mm:ssZ");
                    clients.Add(client);
                }
                catch
                {
                }
            }

            Dictionary<string, object> index = new Dictionary<string, object>();
            index["schemaVersion"] = "1.0";
            index["serverVersion"] = Program.ProductVersion;
            index["generatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            index["clientCount"] = clients.Count;
            index["clients"] = clients;
            return serializer.Serialize(index);
        }

        private static RequestContext ReadRequest(NetworkStream stream)
        {
            MemoryStream buffer = new MemoryStream();
            byte[] temp = new byte[4096];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(temp, 0, temp.Length);
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(temp, 0, read);
                headerEnd = FindHeaderEnd(buffer.ToArray());
            }

            byte[] raw = buffer.ToArray();
            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] firstLine = lines[0].Split(' ');

            RequestContext request = new RequestContext();
            request.Method = firstLine.Length > 0 ? firstLine[0].ToUpperInvariant() : "";
            request.Path = firstLine.Length > 1 ? firstLine[1] : "/";
            request.Headers = new Dictionary<string, string>();

            for (int i = 1; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf(':');
                if (separator > 0)
                {
                    request.Headers[lines[i].Substring(0, separator).Trim().ToLowerInvariant()] = lines[i].Substring(separator + 1).Trim();
                }
            }

            int contentLength = request.Headers.ContainsKey("content-length") ? Convert.ToInt32(request.Headers["content-length"]) : 0;
            int bodyOffset = headerEnd + 4;
            MemoryStream body = new MemoryStream();
            if (raw.Length > bodyOffset)
            {
                body.Write(raw, bodyOffset, raw.Length - bodyOffset);
            }

            while (body.Length < contentLength)
            {
                int read = stream.Read(temp, 0, Math.Min(temp.Length, contentLength - (int)body.Length));
                if (read <= 0)
                {
                    break;
                }
                body.Write(temp, 0, read);
            }

            request.Body = Encoding.UTF8.GetString(body.ToArray());
            return request;
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void SendJson(NetworkStream stream, string json)
        {
            SendText(stream, json, "application/json; charset=utf-8", 200);
        }

        private static void SendUnauthorized(NetworkStream stream)
        {
            byte[] body = Encoding.UTF8.GetBytes("Unauthorized");
            string header = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"Windows Soft Inventory\"\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        private static void SendText(NetworkStream stream, string text, string contentType, int statusCode)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string status = statusCode == 200 ? "OK" : (statusCode == 400 ? "Bad Request" : (statusCode == 401 ? "Unauthorized" : (statusCode == 404 ? "Not Found" : "Error")));
            string header = "HTTP/1.1 " + statusCode + " " + status + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
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

        private sealed class RequestContext
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Headers;
            public string Body;
        }

        private const string DashboardHtml = @"<!doctype html><html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>Windows Soft Inventory</title><link rel=""stylesheet"" href=""/styles.css""></head><body><header class=""topbar""><div><h1>Windows Soft Inventory</h1><p id=""generatedAt"">Waiting for inventory data.</p></div><input id=""searchInput"" type=""search"" placeholder=""Filter computers, OS, Office, software""></header><main><section class=""summary""><div><span id=""clientCount"">0</span><small>Clients</small></div><div><span id=""windowsActivated"">0</span><small>Windows activated</small></div><div><span id=""officeActivated"">0</span><small>Office activated</small></div><div><span id=""staleCount"">0</span><small>Stale &gt;48h</small></div></section><section class=""table-wrap""><table><thead><tr><th>Computer</th><th>OS</th><th>Office</th><th>Windows</th><th>Office activation</th><th>Software</th><th>Collected</th></tr></thead><tbody id=""inventoryBody""></tbody></table></section></main><script src=""/app.js""></script></body></html>";

        private const string DashboardJs = @"(function(){const staleHours=48;const state={clients:[]};function byId(id){return document.getElementById(id)}function text(v){return v===undefined||v===null||v===''?'Unknown':String(v)}function activated(v){return v?'Activated':'Not detected'}function isStale(c){const d=new Date(c.collectedAt||c.sourceUpdatedAt||0);return Number.isNaN(d.getTime())||((Date.now()-d.getTime())/36e5)>staleHours}function matches(c,q){if(!q)return true;const software=(c.software||[]).map(i=>`${i.name} ${i.version}`).join(' ');const h=[c.computerName,c.domain,c.os&&c.os.caption,c.os&&c.os.version,c.office&&c.office.name,c.office&&c.office.version,software].join(' ').toLowerCase();return h.indexOf(q.toLowerCase())!==-1}function summary(clients){byId('clientCount').textContent=clients.length;byId('windowsActivated').textContent=clients.filter(c=>c.activation&&c.activation.windows&&c.activation.windows.activated).length;byId('officeActivated').textContent=clients.filter(c=>c.activation&&c.activation.office&&c.activation.office.activated).length;byId('staleCount').textContent=clients.filter(isStale).length}function table(clients){const q=byId('searchInput').value.trim();const rows=clients.filter(c=>matches(c,q)).map(c=>{const os=c.os||{},office=c.office||{},a=c.activation||{},wa=a.windows||{},oa=a.office||{},count=(c.software||[]).length;return `<tr class=""${isStale(c)?'stale':''}""><td><strong>${text(c.computerName)}</strong><small>${text(c.domain)}</small></td><td>${text(os.caption)}<small>${text(os.version)} build ${text(os.buildNumber)}</small></td><td>${text(office.name)}<small>${text(office.version)}</small></td><td>${activated(wa.activated)}</td><td>${activated(oa.activated)}</td><td>${count}</td><td>${text(c.collectedAt)}</td></tr>`});byId('inventoryBody').innerHTML=rows.join('')||'<tr><td colspan=""7"" class=""empty"">No matching inventory records.</td></tr>'}function render(){summary(state.clients);table(state.clients)}fetch('/api/v1/clients',{cache:'no-store'}).then(r=>{if(!r.ok)throw new Error(`HTTP ${r.status}`);return r.json()}).then(d=>{state.clients=d.clients||[];byId('generatedAt').textContent=`Generated: ${text(d.generatedAt)}`;render()}).catch(e=>{byId('generatedAt').textContent=`Inventory index is not available: ${e.message}`;render()});byId('searchInput').addEventListener('input',render)}());";

        private const string DashboardCss = @":root{--bg:#f5f7fa;--panel:#fff;--text:#17202a;--muted:#5f6b7a;--line:#d9e0e8;--accent:#126f8f;--warn:#fff1c2}*{box-sizing:border-box}body{margin:0;font-family:Segoe UI,Arial,sans-serif;background:var(--bg);color:var(--text)}.topbar{display:flex;gap:24px;align-items:center;justify-content:space-between;padding:24px 32px;background:var(--panel);border-bottom:1px solid var(--line)}h1{margin:0 0 6px;font-size:24px;font-weight:650}p,small{color:var(--muted)}p{margin:0}input[type=search]{width:min(520px,45vw);min-width:280px;height:40px;padding:0 12px;border:1px solid var(--line);border-radius:6px;font:inherit}main{padding:24px 32px}.summary{display:grid;grid-template-columns:repeat(4,minmax(150px,1fr));gap:12px;margin-bottom:18px}.summary div{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:16px}.summary span{display:block;margin-bottom:4px;color:var(--accent);font-size:28px;font-weight:700}.table-wrap{overflow-x:auto;background:var(--panel);border:1px solid var(--line);border-radius:8px}table{width:100%;border-collapse:collapse;min-width:980px}th,td{padding:12px 14px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{background:#edf2f6;font-size:12px;color:var(--muted);text-transform:uppercase}td small{display:block;margin-top:4px}tr.stale td{background:var(--warn)}.empty{padding:28px;text-align:center;color:var(--muted)}@media(max-width:820px){.topbar{align-items:stretch;flex-direction:column;padding:18px}input[type=search]{width:100%;min-width:0}main{padding:18px}.summary{grid-template-columns:repeat(2,minmax(0,1fr))}}";
    }
}
