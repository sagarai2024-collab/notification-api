using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using NotificationApi.Configuration;
using NotificationApi.Data;
using NotificationApi.Middleware;
using NotificationApi.Providers;
using NotificationApi.Repositories;
using NotificationApi.Services;
using NotificationApi.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Notification API", Version = "v1" });
    o.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));

    // Adds the "Authorize" button. It only matters when Auth:ApiKey is set in appsettings.
    o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyMiddleware.HeaderName,
        Description = "Needed only when Auth:ApiKey is configured."
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>()
    });
});

// Settings from appsettings.json
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// Dependency injection (everything is injected through constructors)
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("NotificationDb")));
builder.Services.AddScoped<INotificationRepository, SqlServerNotificationRepository>();
builder.Services.AddSingleton<INotificationProvider, SimulatedEmailProvider>();
builder.Services.AddSingleton<INotificationRequestValidator, NotificationRequestValidator>();
builder.Services.AddScoped<INotificationService, NotificationService>();

if (builder.Configuration.GetValue<bool>($"{NotificationOptions.SectionName}:WorkerEnabled"))
    builder.Services.AddHostedService<NotificationProcessingWorker>();

var app = builder.Build();

// Creates the database if needed and applies all EF migrations (the Notifications table etc.).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Unexpected exceptions become a clean 500 problem response instead of a stack trace.
app.UseExceptionHandler();

app.UseSwagger();
app.UseSwaggerUI();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapControllers();

app.Run();

// Lets the test project use WebApplicationFactory<Program>.
public partial class Program { }
