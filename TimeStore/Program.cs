using System.Text.Json.Serialization;
using TimeStore.Core;
using TimeStore.Core.Database.Entities;
using TimeStore.Core.Raft;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCoreServices(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

var raftService = app.Services.GetRequiredService<IRaftService>();
await raftService.StartAsync();


app.Lifetime.ApplicationStopping.Register(async void () =>
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