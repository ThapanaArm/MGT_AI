using System.Security.Claims;
using System.Text;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using MgtAiAuthen.Api.Options;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using MgtAiAuthen.Api.Services.Reporting;
using MgtAiAuthen.Api.Services.DataSources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- ค่าตั้ง

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection(ClaudeOptions.SectionName));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection(OpenAiOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.SectionName));

string connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is missing from appsettings");

JwtOptions jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("The \"Jwt\" section is missing from appsettings");

if (jwtOptions.SigningKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey must be at least 32 characters — set it in appsettings " +
        "or in the Jwt__SigningKey environment variable.");
}

// ---------------------------------------------------------------- บริการพื้นฐาน

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

// ขีดจำกัด multipart ตั้งต้นของ ASP.NET (128MB) ไม่ตรงกับที่ตั้งไว้ในระบบ
// ตั้งให้เท่ากับ RequestSizeLimit ของ action อัพโหลด เพื่อให้ error ที่ผู้ใช้เห็นสอดคล้องกัน
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 64L * 1024 * 1024;
});
builder.Services.AddMemoryCache();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "MGT AI Authen API",
        Version = "v1",
        Description = "Internal AI chat (Claude) with sign-in, audit logging and question screening",
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token from /api/auth/login (do not type the word Bearer)",
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
        }] = Array.Empty<string>(),
    });
});

// ---------------------------------------------------------------- Authentication / Authorization

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
        };
    });

builder.Services.AddAuthorization();

// ---------------------------------------------------------------- CORS สำหรับ frontend React

const string CorsPolicy = "frontend";
string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    if (allowedOrigins.Length == 0)
    {
        // ไม่ได้ระบุ origin: เปิดกว้าง — ใช้ได้เฉพาะตอน dev
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    }
    else
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("Content-Disposition");
    }
}));

// ---------------------------------------------------------------- บริการของแอป

builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
// Three AI vendors behind one router. Registering each as IAiProvider means adding a fourth
// vendor is a new class plus one line here — ChatService does not change.
builder.Services.AddSingleton<IAiProvider, ClaudeClient>();
builder.Services.AddSingleton<IAiProvider, GeminiClient>();
builder.Services.AddSingleton<IAiProvider, OpenAiClient>();
builder.Services.AddSingleton<IAiClient, AiClient>();

// Gemini and OpenAI are called over plain HTTP, so they need pooled handlers rather than a new
// HttpClient per call (socket exhaustion) or one static instance (stale DNS).
builder.Services.AddHttpClient(GeminiClient.HttpClientName, (services, client) =>
{
    GeminiOptions gemini = services.GetRequiredService<IOptions<GeminiOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(gemini.TimeoutSeconds);
});

builder.Services.AddHttpClient(OpenAiClient.HttpClientName, (services, client) =>
{
    OpenAiOptions openAi = services.GetRequiredService<IOptions<OpenAiOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(openAi.TimeoutSeconds);
});
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IPolicyService, PolicyService>();
builder.Services.AddScoped<ICostCalculator, CostCalculator>();
builder.Services.AddSingleton<ISpreadsheetTextExtractor, SpreadsheetTextExtractor>();
builder.Services.AddSingleton<IAttachmentService, AttachmentService>();

// Report export: one writer per format, resolved by IReportService from the requested format.
builder.Services.AddSingleton<IReportFontProvider, ReportFontProvider>();
builder.Services.AddSingleton<IReportWriter, ExcelReportWriter>();
builder.Services.AddSingleton<IReportWriter, PdfReportWriter>();
builder.Services.AddSingleton<IReportWriter, WordReportWriter>();
builder.Services.AddSingleton<IReportWriter, PowerPointReportWriter>();
builder.Services.AddScoped<IReportService, ReportService>();

// Data source registry (Phase 1 — registry + permissions only, see README 6.22). Secrets are
// encrypted with the Data Protection key ring; on more than one machine that ring must be shared
// (a network path or a key vault) or a second instance cannot decrypt what the first one wrote.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")))
    .SetApplicationName("MgtAiAuthen");

builder.Services.AddSingleton<IDataSourceConnectionTester, LocalFolderConnectionTester>();
builder.Services.AddSingleton<IDataSourceConnectionTester, ApiConnectionTester>();
builder.Services.AddSingleton<IDataSourceConnectionTester, SharePointConnectionTester>();
builder.Services.AddSingleton<IDataSourceConnectionTester, DataLakeConnectionTester>();
builder.Services.AddScoped<IDataSourceService, DataSourceService>();

// Self-service Projects and Skills — any signed-in employee, not just Admin.
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<ISkillService, SkillService>();

builder.Services.AddHttpClient(ApiConnectionTester.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient(SharePointConnectionTester.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IChatLogService, ChatLogService>();
builder.Services.AddScoped<DbSeeder>();

WebApplication app = builder.Build();

// ---------------------------------------------------------------- Pipeline

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "MGT AI Authen API v1"));

    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/api/health", async (AppDbContext db, IAiClient ai, CancellationToken ct) =>
    Results.Ok(new
    {
        Status = "ok",
        Database = await db.Database.CanConnectAsync(ct) ? "connected" : "unavailable",
        AiReady = ai.IsConfigured,
        Model = ai.DefaultModel,
        Providers = ai.Providers,
        ServerTime = DateTime.Now,
    })).ExcludeFromDescription();

// ---------------------------------------------------------------- สร้างผู้ใช้ตั้งต้น

using (IServiceScope scope = app.Services.CreateScope())
{
    DbSeeder seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
    await seeder.SeedAsync();
}

app.Run();
