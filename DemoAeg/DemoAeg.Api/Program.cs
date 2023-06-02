using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services
    .AddEndpointsApiExplorer()
    .AddSwaggerGen();

builder.Services.AddSignalR();
builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/octet-stream" });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("SignalRClientPolicy", policy =>
    {
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .WithOrigins("https://localhost:7230", "https://localhost:7287")
            .AllowCredentials();
    });
});

builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<IUserIdProvider, MyDemoAegIdProvider>();

var app = builder.Build();

app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("SignalRClientPolicy");

const string HEADER_SUBSCRIPTION_NAME = "aeg-subscription-name";
const string SUBSCRIPTION_VALUE = "egt-demo-aeg-es-api";
const string HEADER_SECRET = "demo-aeg-secret";
const string SECRET_VALUE = "123456";
const string EVENTTYPE_TELEPHONIE_DECROCHER = "Demo.Telephonie.Decrocher";

app.MapPost("/webhook/", async (EventGridEvent[] events, HttpRequest request, ILogger<Program> logger, EventHub eventHub) =>
{
    #region Log
    var deliveryCount = request.Headers["aeg-delivery-count"].FirstOrDefault() + 1;
    logger.LogInformation($"Event recu - Type : {events.FirstOrDefault()?.EventType} (tentative {deliveryCount})");
    #endregion Log

    #region Security
    var subscriptionName = request.Headers[HEADER_SUBSCRIPTION_NAME].FirstOrDefault();
    if (subscriptionName?.ToUpperInvariant() != SUBSCRIPTION_VALUE.ToUpperInvariant())
    {
        logger.LogWarning($"Appel webhook forbidden. Nom de souscription incorrect : {subscriptionName}");
        return Results.Forbid();
    }
    var secret = request.Headers[HEADER_SECRET].FirstOrDefault();
    if (secret != SECRET_VALUE)
    {
        logger.LogWarning($"Appel webhook forbidden. Secret incorrect : {secret}");
        return Results.Forbid();
    }
    #endregion Security

    #region Processing
    foreach (var evt in events)
    {
        if (evt.TryGetSystemEventData(out var systemEvent))
        {
            #region Processing > System events
            //System event
            switch (systemEvent)
            {
                case SubscriptionValidationEventData subscriptionValidated:
                    //Requis pour enregistrement Webhook : https://docs.microsoft.com/en-us/azure/event-grid/webhook-event-delivery
                    var response = new SubscriptionValidationResponse
                    {
                        ValidationResponse = subscriptionValidated.ValidationCode
                    };
                    return Results.Ok(response);
                default:
                    var errorMessage = $"Event système non géré : {evt.EventType}";
                    logger.LogWarning(errorMessage, evt.Data.ToString());
                    return Results.UnprocessableEntity();
            }
            #endregion Processing > System events
        }
        else
        {
            #region Processing > Custom events
            //Désérialisation
            var serializer = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReferenceHandler = ReferenceHandler.Preserve
            };
            var eventData = evt.Data.ToObjectFromJson<TelephonieEventData>(serializer);

            switch (evt.EventType)
            {
                case EVENTTYPE_TELEPHONIE_DECROCHER:
                    logger.LogInformation($"Event={evt.EventType}, Destinataire={eventData.AgentLogin}, Appelant={eventData.CustNumber}");
                    var callerName = GetCallerFromDatabase(eventData.CustNumber);
                    await eventHub.SendEventAsync(eventData.AgentLogin, evt.EventType, eventData.CustNumber, eventData.WaitDuration, callerName);
                    return Results.Ok();
                default:
                    var errorMessage = $"Event custom non géré : {evt.EventType}";
                    logger.LogWarning(errorMessage, evt.Data.ToString());
                    return Results.UnprocessableEntity();
            }
            #endregion Processing > Custom events
        }
    }
    #endregion Processing

    return Results.UnprocessableEntity();
})
.WithOpenApi();

app.MapHub<EventHub>("/eventhub");

app.Run();

string GetCallerFromDatabase(string telephone)
{
    //Simulation recherche base de données
    var firsts = new[]
    {
        "François",
        "Denis",
        "Jean",
        "Pierre",
        "Denis",
        "Lucie",
        "Marie",
        "Dominique",
        "Léa",
        "Ghislène"
    };
    var lasts = new[]
    {
        "Martin",
        "Bernard",
        "Thomas",
        "Petit",
        "Robert",
        "Richard",
        "Durand",
        "Dubois",
        "Moreau",
        "Laurent"
    };

    var rnd = new Random();
    var indexFirst = rnd.Next(0, firsts.Length - 1);
    var indexLast = rnd.Next(0, lasts.Length - 1);

    return firsts[indexFirst] + " " + lasts[indexLast].ToUpperInvariant();
}

class TelephonieEventData
{
    public string AgentLogin { get; set; }
    public string CustNumber { get; set; }
    public string WaitDuration { get; set; }
}

internal class SignalrUser
{
    public string ConnectionId { get; set; }
    public string AgentLogin { get; set; }
}

internal class EventHub : Hub
{
    internal static List<SignalrUser> users = new List<SignalrUser>();

    public override Task OnConnectedAsync()
    {
        var agentLogin = Context.GetHttpContext().Request.Query["agentLogin"].FirstOrDefault();
        var connectionID = Context.ConnectionId;
        users.Add(new SignalrUser
        {
            ConnectionId = connectionID,
            AgentLogin = agentLogin
        });

        return base.OnConnectedAsync();
    }


    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var userToRemove = users.First(i => i.ConnectionId == Context.ConnectionId);
        users.Remove(userToRemove);

        return base.OnDisconnectedAsync(exception);
    }

    public async Task SendEventAsync(string toAgentLogin, string eventName, string telephone, string wait, string callerName)
    {
        //Envoi ciblé au seul destinataire
        var toConnectionId = users.FirstOrDefault(i => i.AgentLogin == toAgentLogin)?.ConnectionId;
        if (toConnectionId != null)
        {
            await Clients.User(toConnectionId).SendAsync(eventName, telephone, wait, callerName);
        }
        else
        {
            //Agent non-connecté, ignoré
        }
    }
}


internal class MyDemoAegIdProvider : IUserIdProvider
{
    //Par défaut SignalR utilise JWT/cookies, la démo étant simpliste nous simulons un utilisateur connecté
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.ConnectionId;
    }
}
