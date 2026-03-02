using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using LogSystem.Dashboard.Converters;
using LogSystem.Dashboard.Data;
using LogSystem.Dashboard.Filters;

var builder = WebApplication.CreateBuilder(args);

// ─── Firebase / Firestore (Optional for local-only mode) ───
var firebaseCredPath = builder.Configuration["Firebase:CredentialPath"]
    ?? "firebase-service-account.json";

// Resolve to absolute path relative to content root
if (!Path.IsPathRooted(firebaseCredPath))
    firebaseCredPath = Path.Combine(builder.Environment.ContentRootPath, firebaseCredPath);

bool useFirestore = File.Exists(firebaseCredPath);

if (useFirestore)
{
    var firebaseProjectId = builder.Configuration["Firebase:ProjectId"]
        ?? throw new InvalidOperationException("Firebase:ProjectId is required in configuration.");

    // Set GOOGLE_APPLICATION_CREDENTIALS so all Google SDKs pick it up
    Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", firebaseCredPath);

    // Initialize Firebase Admin SDK (used for auth / optional features)
    if (FirebaseApp.DefaultInstance == null)
    {
        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromFile(firebaseCredPath),
            ProjectId = firebaseProjectId
        });
    }

    // Initialize Firestore client
    var firestoreDb = new FirestoreDbBuilder
    {
        ProjectId = firebaseProjectId,
        CredentialsPath = firebaseCredPath
    }.Build();

    builder.Services.AddSingleton(firestoreDb);
    builder.Services.AddSingleton<FirestoreService>();
    Console.WriteLine($"✓ Firestore enabled - Project: {firebaseProjectId}");
}
else
{
    Console.WriteLine("⚠️  Running in LOCAL-ONLY mode (no Firestore persistence)");
    Console.WriteLine($"   Place 'firebase-service-account.json' in {builder.Environment.ContentRootPath} to enable Firestore");
}

builder.Services.AddSingleton<InMemoryStore>();
builder.Services.AddMemoryCache();

// Controllers — with Firestore Timestamp JSON converter and error handling
builder.Services.AddControllers(opts =>
    {
        opts.Filters.Add<FirestoreExceptionFilter>();
    })
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.Converters.Add(new FirestoreTimestampJsonConverter());
    });

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LogSystem Dashboard API", Version = "v1" });
});

// CORS — allow dashboard frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("Dashboard", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Verify Firestore connection on startup (non-fatal — quota/transient errors shouldn't block startup)
if (useFirestore)
{
    try
    {
        var fs = app.Services.GetRequiredService<FirestoreDb>();
        var collections = await fs.ListRootCollectionsAsync().ToListAsync();
        app.Logger.LogInformation("Connected to Firestore project: {ProjectId} ({Count} collections)", 
            fs.ProjectId, collections.Count);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Firestore connectivity check failed (may be quota/transient). Dashboard will start anyway.");
    }
}

// Middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Dashboard");

// Serve static files for the dashboard UI (before routing)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();
app.MapControllers();

app.Run();


