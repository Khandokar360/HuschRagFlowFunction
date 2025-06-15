using HuschRagFlowEngineFunctionApp;
using HuschRagFlowEngineFunctionApp.Service;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartComponents.LocalEmbeddings;
using Syncfusion.Blazor.SmartComponents;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Add required services
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Register LocalEmbedder as singleton
builder.Services.AddSingleton<LocalEmbedder>();

// Configure Syncfusion SmartComponents with OpenAI credentials
builder.Services.AddSingleton<AIServiceCredentials>(provider =>
{
    var configuration = provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();

    // Get configuration values with better error messages
    var apiKey = configuration["AzureOpenAI.ApiKey"];
    var deploymentName = configuration["AzureOpenAI.DeploymentName"];
    var endpoint = configuration["AzureOpenAI.ChatCompletion.Endpoint"];

    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("AzureOpenAI.ApiKey is required. Please add it to your configuration (appsettings.json or user secrets).");

    if (string.IsNullOrWhiteSpace(deploymentName))
        throw new InvalidOperationException("AzureOpenAI.DeploymentName is required. Please add it to your configuration (appsettings.json or user secrets).");

    if (string.IsNullOrWhiteSpace(endpoint))
        throw new InvalidOperationException("AzureOpenAI.ChatCompletion.Endpoint is required. Please add it to your configuration (appsettings.json or user secrets).");

    return new AIServiceCredentials
    {
        ApiKey = apiKey,
        DeploymentName = deploymentName,
        Endpoint = new Uri(endpoint)
    };
});

// Register OpenAI configuration and Azure AI service
builder.Services.AddSingleton<OpenAIConfiguration>();
builder.Services.AddSingleton<AzureAIService>();

// Register the function as transient (functions should be transient)
builder.Services.AddTransient<ProcessComplaintFunction>();

builder.Build().Run();