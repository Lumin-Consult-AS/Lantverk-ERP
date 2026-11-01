using Erp.Sdk;

namespace Invoice.Claude;

public static class InvoiceAppProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection(ClaudeOptions.Section));
        builder.Services.AddHttpClient<IInvoiceExtractor, ClaudeInvoiceExtractor>(client => client.Timeout = TimeSpan.FromSeconds(120));
        builder.Services.AddErpApp<InvoiceApp>(options =>
        {
            options.CoreGrpcAddress = builder.Configuration["Erp:CoreGrpcAddress"] ?? "http://erp-core:5000";
        });

        var app = builder.Build();

        app.MapErpApp<InvoiceApp>();
        app.MapGet("/health", () => Results.Ok(new { app = "invoice.claude", status = "healthy" }));

        app.Run();
    }
}
