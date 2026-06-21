# CloudflareDdns

A free, self-hosted replacement for the **Dynu DDNS service + Dynu IP Update Client** — built as a
**.NET 8 Windows Service** in C#.

Every hour it:

1. Resolves this machine's current **public IPv4** (your Quantum Fiber WAN address) from external
   "what's my IP" providers, with fallback.
2. Discovers the **hostnames to manage** — read from your **IIS site bindings** (host headers) and/or a
   static list in config.
3. For each hostname, finds the owning **Cloudflare zone** and updates the **A record** to your current
   IP (only when it actually changed).
4. **Logs** everything to a rolling file and the Windows Event Log so you can monitor it.

It only ever talks to Cloudflare when your IP changes, so it's gentle on the API.

---

## How it works

```
PeriodicTimer (hourly + on startup)
        │
        ▼
   DdnsUpdater.RunOnceAsync
        │
        ├── PublicIpProvider   → ipify / aws / icanhazip / ifconfig.me  (first valid IPv4 wins)
        ├── HostnameProvider   → IIS bindings (Microsoft.Web.Administration) and/or static list
        ├── StateStore         → skip everything if IP == last cached IP
        └── CloudflareClient   → list zones → match hostname → GET A record → PUT if different
```

| Concern        | Where                                                            |
|----------------|-----------------------------------------------------------------|
| Config         | [`appsettings.json`](appsettings.json) → `Ddns` section          |
| Logs (file)    | `C:\ProgramData\CloudflareDdns\logs\ddns-*.log` (30-day rolling) |
| Logs (events)  | Windows **Event Viewer → Application**, source `CloudflareDdns`  |
| Last-known IP  | `C:\ProgramData\CloudflareDdns\state.json`                       |

---

## Setup

### 1. Get a Cloudflare API token

Dashboard → **My Profile → API Tokens → Create Token**. Use the **Edit zone DNS** template, scoped to
the zone(s) you want managed. The token needs:

- **Zone → Zone → Read**
- **Zone → DNS → Edit**

### 2. Configure

Put your **secrets and personal hostnames** in a local override file that is **git-ignored**, so they
never end up in source control. Copy the example and edit it:

```powershell
Copy-Item appsettings.local.example.json appsettings.local.json
```

```jsonc
// appsettings.local.json  (git-ignored; overrides appsettings.json)
"Ddns": {
  "ApiToken": "your-cloudflare-token",
  "HostnameSource": "Static",      // Iis | Static | Both
  "Hostnames": [ "home.example.com" ], // used for Static / Both
  "ExcludeHostnames": [ "localhost" ],
  "Proxied": true,                 // true = orange-cloud the records
  "CreateIfMissing": false         // true = create A records that don't exist yet
}
```

The committed [`appsettings.json`](appsettings.json) holds the non-secret defaults (interval, IP
providers, logging) and placeholders. Anything in `appsettings.local.json` wins.

> **Alternative:** set just the token via a machine env var instead of the file:
> `setx Ddns__ApiToken "your-token" /M`.

**Config keys:** `Interval` (TimeSpan, default `01:00:00`), `HostnameSource` (`Iis`/`Static`/`Both`),
`Hostnames`, `ExcludeHostnames`, `Proxied`, `CreateIfMissing`, `DryRun`. `Proxied`/`RecordTtl` apply
only to records the service *creates* — updates change just the IP and preserve the record's existing
proxy flag and TTL.

### 3. Install the service

From an **elevated** PowerShell prompt in this folder:

```powershell
.\install.ps1
```

This publishes to `C:\Program Files\CloudflareDdns`, registers the service (auto-start, auto-restart on
failure), and starts it. After editing config later:

```powershell
Restart-Service CloudflareDdns
```

### 4. Verify

```powershell
Get-Service CloudflareDdns
Get-Content 'C:\ProgramData\CloudflareDdns\logs\ddns-*.log' -Tail 30 -Wait
```

You should see lines like:

```
Managing 2 hostname(s): www.example.com, api.example.com
Public IP is 75.x.x.x (was unknown); reconciling 2 hostname(s).
Updated A record www.example.com: 75.y.y.y -> 75.x.x.x (zone example.com).
```

---

## Dry run — preview changes safely

Before letting it touch your DNS, do a read-only preview. With a **real API token** set, run:

```powershell
dotnet run -c Release -- --dry-run
```

It does **one** pass and exits, logging exactly what it would do without writing anything to Cloudflare:

```
[DRY RUN] No changes will be written to Cloudflare.
Managing 2 hostname(s): www.example.com, api.example.com
Public IP is 71.x.x.x; reconciling 2 hostname(s).
[DRY RUN] Would update A record www.example.com: 71.y.y.y -> 71.x.x.x (zone example.com).
A record api.example.com already points to 71.x.x.x (zone example.com); no change.
[DRY RUN] Reconcile preview complete: 1 record(s) would change.
```

This is the safe way to verify your IIS→zone→record matching is correct. Dry-run bypasses the
"IP unchanged" shortcut so it always shows the full mapping, and it never advances the cached IP.
You can also set `"DryRun": true` in `appsettings.json` (e.g. to install the service in preview mode).

## Run in the foreground (debugging)

You can run it as a normal console app without installing the service:

```powershell
dotnet run -c Release
```

With the placeholder token still in place it will fail fast with a clear config error — that's expected.

## Uninstall

```powershell
.\uninstall.ps1            # remove the service
.\uninstall.ps1 -RemoveFiles   # also delete binaries + data/logs
```

---

## Notes & behavior

- **IIS discovery** reads host headers from every site binding. Bindings with no host header (IP-only),
  wildcards (`*.example.com`), and single-label names (`localhost`) are skipped automatically.
- A hostname is only updated if a **Cloudflare zone you can see owns it**. Unowned hostnames are logged
  and skipped, never created (unless `CreateIfMissing` is on).
- If a run **partially fails**, the cached IP is *not* advanced, so the next hourly run retries the
  failed hosts instead of assuming everything is current.
- IPv4 only (A records). AAAA/IPv6 isn't handled.
- Requires the service account (LocalSystem by default) to be able to read IIS config — LocalSystem can.

---

## License

[MIT](LICENSE) © David Veksler
