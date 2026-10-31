using Erp.Sdk;

namespace Bank.Iso20022;

public static class BankAppProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddErpApp<BankApp>(options =>
        {
            options.CoreGrpcAddress = builder.Configuration["Erp:CoreGrpcAddress"] ?? "http://erp-core:5000";
        });

        var app = builder.Build();

        app.MapErpApp<BankApp>();
        app.MapGet("/health", () => Results.Ok(new { app = "bank.iso20022", status = "healthy" }));

        app.Run();
    }
}
