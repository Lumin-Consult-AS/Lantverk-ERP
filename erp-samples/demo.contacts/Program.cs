using Erp.Sdk;

namespace Demo.Contacts;

public static class ContactsAppProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddErpApp<ContactsApp>(options =>
        {
            options.CoreGrpcAddress = builder.Configuration["Erp:CoreGrpcAddress"] ?? "http://erp-core:5000";
        });

        var app = builder.Build();

        app.MapErpApp<ContactsApp>();
        app.MapGet("/health", () => Results.Ok(new { app = "demo.contacts", status = "healthy" }));

        app.Run();
    }
}
