using Erp.Sdk;

namespace Finance;

public static class FinanceAppProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddErpApp<FinanceApp>(options =>
        {
            options.CoreGrpcAddress = builder.Configuration["Erp:CoreGrpcAddress"] ?? "http://erp-core:5000";
        });

        var app = builder.Build();

        app.MapErpApp<FinanceApp>();
        app.MapGet("/health", () => Results.Ok(new { app = "finance", status = "healthy" }));

        app.Run();
    }
}
