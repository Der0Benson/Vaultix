using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Vaultix.Shared;
using Vaultix.Storage;

var builder = WebApplication.CreateBuilder(args);
var repositoryPath = Environment.GetEnvironmentVariable("VAULTIX_REPOSITORY")
    ?? builder.Configuration["Vaultix:RepositoryPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "repository");
var maximumUploadBytes = builder.Configuration.GetValue<long?>("Vaultix:MaximumUploadBytes") ?? 512L * 1024 * 1024 * 1024;
var pairingEnabled = builder.Configuration.GetValue<bool?>("Vaultix:PairingEnabled") ?? builder.Environment.IsDevelopment();

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maximumUploadBytes);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maximumUploadBytes);
builder.Services.AddSingleton(new VaultixRepository(repositoryPath));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();
var repository = app.Services.GetRequiredService<VaultixRepository>();
await repository.InitializeAsync();

app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception is ArgumentException or InvalidDataException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
    context.Response.StatusCode = status;
    await Results.Problem(
        statusCode: status,
        title: status == 400 ? "Ungültige Anfrage" : "Repository-Fehler",
        detail: status == 400 ? exception?.Message : "Die Anfrage konnte nicht sicher abgeschlossen werden.")
        .ExecuteAsync(context);
}));

var api = app.MapGroup(VaultixProtocol.ApiPrefix);
api.MapGet("/health", (VaultixRepository store) => Results.Ok(new HealthResponse(
    "healthy", typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0", Directory.Exists(store.RootPath), store.GetAvailableBytes())));

api.MapPost("/devices/pair", async (PairDeviceRequest request, VaultixRepository store, CancellationToken cancellationToken) =>
    pairingEnabled
        ? Results.Ok(await store.PairDeviceAsync(request.DeviceName, cancellationToken))
        : Results.NotFound());

api.MapPost("/objects/check", async (HttpContext context, ObjectCheckRequest request, VaultixRepository store, CancellationToken cancellationToken) =>
{
    var deviceId = await AuthenticateAsync(context, store, cancellationToken);
    if (deviceId is null)
    {
        return Results.Unauthorized();
    }

    if (request.Hashes.Count > 10_000)
    {
        return Results.BadRequest("Zu viele Hashes in einer Anfrage.");
    }

    var missing = request.Hashes
        .Select(ContentAddressedObjectStore.NormalizeHash)
        .Where(hash => !store.ObjectExists(hash))
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    return Results.Ok(new ObjectCheckResponse(missing));
});

api.MapPut("/objects/{hash}", async (HttpContext context, string hash, VaultixRepository store, CancellationToken cancellationToken) =>
{
    var deviceId = await AuthenticateAsync(context, store, cancellationToken);
    if (deviceId is null)
    {
        return Results.Unauthorized();
    }

    if (context.Request.ContentLength > maximumUploadBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var bytes = await store.StoreObjectAsync(hash, context.Request.Body, maximumUploadBytes, cancellationToken);
    return Results.Ok(new { hash = ContentAddressedObjectStore.NormalizeHash(hash), size = bytes });
});

api.MapPost("/snapshots", async (HttpContext context, CreateSnapshotRequest request, VaultixRepository store, CancellationToken cancellationToken) =>
{
    var deviceId = await AuthenticateAsync(context, store, cancellationToken);
    return deviceId is null
        ? Results.Unauthorized()
        : Results.Ok(await store.CreateSnapshotAsync(deviceId.Value, request, cancellationToken));
});

api.MapGet("/snapshots", async (HttpContext context, VaultixRepository store, CancellationToken cancellationToken) =>
{
    var deviceId = await AuthenticateAsync(context, store, cancellationToken);
    return deviceId is null
        ? Results.Unauthorized()
        : Results.Ok(await store.ListSnapshotsAsync(deviceId.Value, cancellationToken));
});

api.MapGet("/snapshots/{snapshotId:guid}", async (HttpContext context, Guid snapshotId, VaultixRepository store, CancellationToken cancellationToken) =>
{
    var deviceId = await AuthenticateAsync(context, store, cancellationToken);
    if (deviceId is null)
    {
        return Results.Unauthorized();
    }

    var snapshot = await store.GetSnapshotAsync(deviceId.Value, snapshotId, cancellationToken);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

api.MapGet("/snapshots/{snapshotId:guid}/files", async (
    HttpContext context, Guid snapshotId, string path, VaultixRepository store, CancellationToken cancellationToken) =>
{
    var deviceId = await AuthenticateAsync(context, store, cancellationToken);
    if (deviceId is null)
    {
        return Results.Unauthorized();
    }

    var result = await store.OpenSnapshotFileAsync(deviceId.Value, snapshotId, path, cancellationToken);
    return result is null
        ? Results.NotFound()
        : Results.File(result.Value.Stream, "application/octet-stream", Path.GetFileName(result.Value.Entry.RelativePath), enableRangeProcessing: true);
});

app.Run();

static async Task<Guid?> AuthenticateAsync(HttpContext context, VaultixRepository repository, CancellationToken cancellationToken)
{
    if (!Guid.TryParse(context.Request.Headers[VaultixProtocol.DeviceIdHeader], out var deviceId))
    {
        return null;
    }

    var secret = context.Request.Headers[VaultixProtocol.DeviceSecretHeader].ToString();
    return await repository.AuthenticateAsync(deviceId, secret, cancellationToken) ? deviceId : null;
}

public partial class Program;
