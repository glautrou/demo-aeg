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

var app = builder.Build();

app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

const string HEADER_SUBSCRIPTION_NAME = "aeg-subscription-name";
const string SUBSCRIPTION_VALUE = "egt-demo-aeg-es-api";
const string HEADER_SECRET = "demo-aeg-secret";
const string SECRET_VALUE = "123456";
const string EVENTTYPE_TELEPHONIE_DECROCHER = "Demo.Telephonie.Decrocher";

app.MapPost("/webhook/", (EventGridEvent[] events, HttpRequest request, ILogger<Program> logger) =>
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
                    logger.LogInformation($"Event={EVENTTYPE_TELEPHONIE_DECROCHER}, Destinataire={eventData.AgentLogin}, Appelant={eventData.CustNumber}");
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

class TelephonieEventData
{
    public string AgentLogin { get; set; }
    public string CustNumber { get; set; }
    public string WaitDuration { get; set; }
}

public class EventHub : Hub
{
    public async Task SendEvent(string toAgentLogin, string eventName, string telephone, int wait, string callerName)
    {
        //Envoi ciblÃ© au seul destinataire
        await Clients.User(toAgentLogin).SendAsync(eventName, telephone, wait, callerName);
    }
}