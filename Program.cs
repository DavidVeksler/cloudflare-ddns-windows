using System.Net.Http.Headers;
using CloudflareDdns;
using CloudflareDdns.Configuration;
using CloudflareDdns.Services;
using Serilog;

// Bootstrap a fail-safe logger so config/startup errors are never swallowed silently.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Local overrides for secrets / personal config (token, real hostnames). Git-ignored,
    // so it keeps those out of source control. Wins over appsettings.json.
    builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

    // Friendly bare flag: `--dry-run` (or `/dry-run`) forces Ddns:DryRun on.
    // Added last so it overrides appsettings.json.
    if (args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("/dry-run", StringComparison.OrdinalIgnoreCase)))
    {
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Ddns:DryRun"] = "true" });
    }

    // Run as a Windows Service when started by the SCM; runs as a console app otherwise.
    builder.Services.AddWindowsService(o => o.ServiceName = "CloudflareDdns");

    // Serilog reads its sinks (file + event log + console) from appsettings.json.
    builder.Services.AddSerilog((sp, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext());

    // Bind & validate the Ddns config section.
    builder.Services.AddOptions<DdnsOptions>()
        .Bind(builder.Configuration.GetSection(DdnsOptions.SectionName))
        .Validate(o =>
        {
            o.Validate();
            return true;
        }, "Invalid Ddns configuration.")
        .ValidateOnStart();

    var options = builder.Configuration
        .GetSection(DdnsOptions.SectionName)
        .Get<DdnsOptions>() ?? new DdnsOptions();

    // HttpClient for IP lookups: short timeout, identifiable user agent.
    builder.Services.AddHttpClient("ip", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CloudflareDdns/1.0");
    });

    // HttpClient for Cloudflare: base address + bearer auth baked in.
    builder.Services.AddHttpClient("cloudflare", c =>
    {
        c.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiToken);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CloudflareDdns/1.0");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    });

    builder.Services.AddSingleton<IPublicIpProvider, PublicIpProvider>();
    builder.Services.AddSingleton<IHostnameProvider, HostnameProvider>();
    builder.Services.AddSingleton<ICloudflareClient, CloudflareClient>();
    builder.Services.AddSingleton<StateStore>();
    builder.Services.AddSingleton<DdnsUpdater>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "CloudflareDdns terminated unexpectedly during startup.");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
