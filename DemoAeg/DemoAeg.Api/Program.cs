using Microsoft.Azure.EventGrid.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/", () =>
{
    return "OK";
})
.WithOpenApi();

const string HEADER_SUBSCRIPTION_NAME = "aeg-subscription-name";
const string SUBSCRIPTION_VALUE = "DEMO-API";
const string HEADER_SECRET = "demo-secret";
const string SECRET_VALUE = "123456";

app.MapPost("/webhook/", (EventGridEvent[] events, HttpRequest request, ILogger logger) =>
{
    //Log
    var deliveryCount = request.Headers["aeg-delivery-count"].FirstOrDefault() + 1;
    logger.LogInformation($"Event recu - Type : {events.FirstOrDefault()?.EventType} (tentative {deliveryCount})");

    //Security
    var subscriptionName = request.Headers[HEADER_SUBSCRIPTION_NAME].FirstOrDefault();
    if (subscriptionName != SUBSCRIPTION_VALUE)
    {
        logger.LogWarning($"Appel webhook forbidden. Nom de souscription incorrect : {subscriptionName}");
        return StatusCodes.Status403Forbidden;
    }
    var secret = request.Headers[HEADER_SECRET].FirstOrDefault();
    if (secret != SECRET_VALUE)
    {
        logger.LogWarning($"Appel webhook forbidden. Secret incorrect : {secret}");
        return StatusCodes.Status403Forbidden;
    }

    return StatusCodes.Status200OK;
})
.WithOpenApi();

app.Run();