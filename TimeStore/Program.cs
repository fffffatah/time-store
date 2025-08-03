using System.Text.Json.Serialization;
using TimeStore.Core;
using TimeStore.Core.Database;
using TimeStore.Core.Raft;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add core services with configuration
builder.Services.AddCoreServices(builder.Configuration);

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// Configure the HTTP request pipeline
// Enable Swagger in all environments
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Start the Raft service
var raftService = app.Services.GetRequiredService<IRaftService>();
await raftService.StartAsync();

// Register a graceful shutdown handler
app.Lifetime.ApplicationStopping.Register(async () =>
{
    await raftService.StopAsync();
});

app.Run();

[JsonSerializable(typeof(Data))]
[JsonSerializable(typeof(List<Data>))]
[JsonSerializable(typeof(RequestVoteRequest))]
[JsonSerializable(typeof(RequestVoteResponse))]
[JsonSerializable(typeof(AppendEntriesRequest))]
[JsonSerializable(typeof(AppendEntriesResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}