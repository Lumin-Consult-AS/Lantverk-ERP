using Erp.Sdk;

namespace Ehf;

public static class EhfAppProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // The access point is chosen and registered here once there is one (ADR-0031); until then nothing is sent or fetched
        builder.Services.AddSingleton<IAccessPoint, NotConfiguredAccessPoint>();
        builder.Services.AddErpApp<EhfApp>(options =>
        {
            options.CoreGrpcAddress = builder.Configuration["Erp:CoreGrpcAddress"] ?? "http://erp-core:5000";
        });

        var app = builder.Build();

        app.MapErpApp<EhfApp>();
        app.MapGet("/health", () => Results.Ok(new { app = "ehf", status = "healthy" }));

        app.Run();
    }
}
