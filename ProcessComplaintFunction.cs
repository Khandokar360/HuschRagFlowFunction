using Azure.Storage.Blobs;
using HuschRagFlowEngineFunctionApp.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartComponents.LocalEmbeddings;
using HuschRagFlowEngineFunctionApp.Service;
using Azure.Storage.Blobs.Models;

namespace HuschRagFlowEngineFunctionApp
{
    public class ProcessComplaintFunction
    {
        private readonly ILogger<ProcessComplaintFunction> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly LocalEmbedder _embedder;
        private readonly AzureAIService _azureAIService;

        private readonly string _connectionStringBlobStorage;
        private readonly string _chatCompletionEndpoint;
        private readonly string _azureOpenAIApiKey;

        // Define matter type mappings as static readonly
        private static readonly Dictionary<string, string> MatterTypeContainerMappings = new()
        {
            { "Glyphosate Matter", "glyphosate-complaintfile" },
            { "Asbestos NonMidwest Matter", "asbestos-nmidwest-complaintfile" },
            { "Paraquat Matter", "paraquat-complaintfile" },
            { "Talc Matter", "talc-complaintfile" }
        };

        public ProcessComplaintFunction(
            ILogger<ProcessComplaintFunction> logger,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IMemoryCache memoryCache,
            LocalEmbedder embedder,
            AzureAIService azureAIService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _cache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
            _azureAIService = azureAIService ?? throw new ArgumentNullException(nameof(azureAIService));

            // Load configuration values with validation
            _chatCompletionEndpoint = _configuration["AzureOpenAI.ChatCompletion.Endpoint"]
                ?? throw new InvalidOperationException("AzureOpenAI.ChatCompletion.Endpoint is required");
            _azureOpenAIApiKey = _configuration["AzureOpenAI.ApiKey"]
                ?? throw new InvalidOperationException("AzureOpenAI.ApiKey is required");
            _connectionStringBlobStorage = _configuration["Azure.BlobStorage.ConnectionString"]
                ?? throw new InvalidOperationException("Azure.BlobStorage.ConnectionString is required");
        }

        [Function("ProcessComplaint")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("ProcessComplaint function triggered");

            try
            {
                // Validate the request is multipart/form-data
                if (!req.HasFormContentType)
                {
                    _logger.LogWarning("Invalid content type. Expected multipart/form-data");
                    return new BadRequestObjectResult("Request must be 'multipart/form-data' with a PDF file and JSON question(s).");
                }

                // Get the uploaded PDF and validate
                var formData = await req.ReadFormAsync();
                var pdfFile = formData.Files.GetFile("file");

                if (pdfFile == null || pdfFile.Length == 0)
                {
                    _logger.LogWarning("No file uploaded or file is empty");
                    return new BadRequestObjectResult("Please upload a PDF file using 'file' as the form field name.");
                }

                // Validate file type
                if (!pdfFile.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"Invalid file type: {pdfFile.ContentType}. Expected PDF");
                    return new BadRequestObjectResult("Please upload a PDF file.");
                }

                // Get the question data
                if (!formData.TryGetValue("data", out var questionValues) || string.IsNullOrWhiteSpace(questionValues.FirstOrDefault()))
                {
                    _logger.LogWarning("No question data provided");
                    return new BadRequestObjectResult("Please provide 'data' in the form data.");
                }

                string questionPayload = questionValues.FirstOrDefault()!;

                // Determine container name based on matter type
                string? blobContainerName = DetermineBlobContainerName(questionPayload);
                if (string.IsNullOrEmpty(blobContainerName))
                {
                    _logger.LogWarning($"Invalid matter type in payload: {questionPayload}");
                    return new BadRequestObjectResult("Invalid matter type in the question payload.");
                }

                // Upload file to blob storage
                string blobUri = await UploadFileAsync(pdfFile, pdfFile.FileName, blobContainerName);
                _logger.LogInformation($"File uploaded successfully: {blobUri}");

                // Parse questions
                List<QuestionConfig> questions = await ParseQuestionsAsync(questionPayload);
                if (!questions.Any())
                {
                    _logger.LogWarning("No valid questions found in payload");
                    return new BadRequestObjectResult("No valid questions provided.");
                }

                // Generate PDF summary
                var pdfViewerModel = new PDFViewerModel(_embedder, _azureAIService);
                string summary = await SummaryPDF(pdfFile, pdfViewerModel);

                _logger.LogInformation("PDF summary generated successfully");

                return new OkObjectResult(new
                {
                    Summary = summary,
                    BlobUri = blobUri,
                    QuestionsCount = questions.Count
                });
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "Invalid argument provided");
                return new BadRequestObjectResult(ex.Message);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON parsing error");
                return new BadRequestObjectResult("Invalid JSON format in question payload.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred processing the complaint");
                return new StatusCodeResult(500);
            }
        }

        private string? DetermineBlobContainerName(string questionPayload)
        {
            return MatterTypeContainerMappings.FirstOrDefault(kvp =>
                questionPayload.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        public async Task<string> UploadFileAsync(IFormFile file, string fileName, string containerName)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("File is empty or null.", nameof(file));

            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("File name cannot be empty.", nameof(fileName));

            if (string.IsNullOrWhiteSpace(containerName))
                throw new ArgumentException("Container name cannot be empty.", nameof(containerName));

            try
            {
                var blobServiceClient = new BlobServiceClient(_connectionStringBlobStorage);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

                // Generate unique filename to avoid conflicts
                string uniqueFileName = $"{Guid.NewGuid()}_{fileName}";
                var blobClient = containerClient.GetBlobClient(uniqueFileName);

                // Upload with proper content type
                using var stream = file.OpenReadStream();
                await blobClient.UploadAsync(stream, new BlobHttpHeaders { ContentType = file.ContentType });

                _logger.LogInformation($"File uploaded to Blob Storage: {uniqueFileName}");
                return blobClient.Uri.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload file to blob storage");
                throw new InvalidOperationException("Failed to upload file to blob storage.", ex);
            }
        }

        private async Task<List<QuestionConfig>> ParseQuestionsAsync(string questionPayload)
        {
            var questions = new List<QuestionConfig>();

            try
            {
                if (string.IsNullOrWhiteSpace(questionPayload))
                {
                    return questions;
                }

                var trimmedPayload = questionPayload.Trim();

                // Fast path for direct QuestionConfig array
                if (trimmedPayload.StartsWith("[{"))
                {
                    var parsedQuestions = JsonConvert.DeserializeObject<List<QuestionConfig>>(trimmedPayload);
                    return parsedQuestions ?? new List<QuestionConfig>();
                }

                // Try to parse the nested MatterTypes structure
                if (trimmedPayload.StartsWith("{"))
                {
                    var matterTypesRequest = JsonConvert.DeserializeObject<MatterTypesRequest>(trimmedPayload);
                    if (matterTypesRequest?.MatterTypes != null && matterTypesRequest.MatterTypes.Any())
                    {
                        questions = matterTypesRequest.MatterTypes
                            .SelectMany(mt => mt.Value?.Questions ?? Enumerable.Empty<QuestionConfig>())
                            .Where(q => q != null && !string.IsNullOrWhiteSpace(q.QuestionText))
                            .ToList();
                    }
                }

                // Fallback: if it's not JSON in that format, check if it is an array of simple strings
                if (!questions.Any() && trimmedPayload.StartsWith("["))
                {
                    var simpleQuestions = JsonConvert.DeserializeObject<List<string>>(trimmedPayload);
                    questions = simpleQuestions?
                        .Where(q => !string.IsNullOrWhiteSpace(q))
                        .Select(q => new QuestionConfig { QuestionText = q })
                        .ToList() ?? new List<QuestionConfig>();
                }

                // Final fallback: treat as raw string if no questions found yet
                if (!questions.Any() && !string.IsNullOrWhiteSpace(trimmedPayload))
                {
                    questions.Add(new QuestionConfig { QuestionText = trimmedPayload });
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse JSON question payload, treating as raw string");
                if (!string.IsNullOrWhiteSpace(questionPayload))
                {
                    questions.Add(new QuestionConfig { QuestionText = questionPayload });
                }
            }

            return questions;
        }

        public static async Task<string> SummaryPDF(IFormFile pdfFile, PDFViewerModel pdfViewerModel)
        {
            const string systemPrompt = "You are a helpful assistant. Your task is to analyze the provided text and generate a concise summary.";

            try
            {
                using var stream = pdfFile.OpenReadStream();
                await pdfViewerModel.LoadDocument(stream, "application/pdf");

                var result = await pdfViewerModel.FetchResponseFromAIService(systemPrompt);

                return string.IsNullOrWhiteSpace(result) ? "No summary generated." : result;
            }
            catch (Exception ex)
            {
                return $"Error generating summary: {ex.Message}";
            }
        }
    }
}