using Erp.Sdk;

namespace Norway;

public static class NorwayAppProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddErpApp<NorwayApp>(options =>
        {
            options.CoreGrpcAddress = builder.Configuration["Erp:CoreGrpcAddress"] ?? "http://erp-core:5000";
        });

        var app = builder.Build();

        app.MapErpApp<NorwayApp>();
        app.MapGet("/health", () => Results.Ok(new { app = "norway", status = "healthy" }));

        app.Run();
    }
}
