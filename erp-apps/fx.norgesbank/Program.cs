using Erp.Sdk;

namespace Fx.NorgesBank;

public static class FxAppProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddHttpClient<IExchangeRateFeed, NorgesBankFeed>(client =>
        {
            client.BaseAddress = new Uri("https://data.norges-bank.no/");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        builder.Services.AddErpApp<FxApp>(options =>
        {
            options.CoreGrpcAddress = builder.Configuration["Erp:CoreGrpcAddress"] ?? "http://erp-core:5000";
        });

        var app = builder.Build();

        app.MapErpApp<FxApp>();
        app.MapGet("/health", () => Results.Ok(new { app = "fx.norgesbank", status = "healthy" }));

        app.Run();
    }
}
