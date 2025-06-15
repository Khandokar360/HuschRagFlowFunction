using Azure.Storage.Blobs;
using HuschRagFlowEngineFunctionApp.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HuschRagFlowEngineFunctionApp
{
    public class ProcessComplaintFunction
    {
        private readonly ILogger<ProcessComplaintFunction> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
    //    private readonly PDFViewerModel _pdfViewerModel;

        private readonly string _connectionStringBlobStorage;
        private string _blobContainerName;
        private readonly string _chatCompletionEndpoint;
        private readonly string _azureOpenAIApiKey;

     public ProcessComplaintFunction(
    ILogger<ProcessComplaintFunction> logger,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _cache = memoryCache;

            // Load configuration values.       
            _chatCompletionEndpoint = _configuration["AzureOpenAI.ChatCompletion.Endpoint"];
            _azureOpenAIApiKey = _configuration["AzureOpenAI.ApiKey"];         
            _connectionStringBlobStorage = _configuration["Azure.BlobStorage.ConnectionString"];
            _blobContainerName = "";
        }





        [Function("ProcessComplaint")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            try
            {
                // Validate the request is multipart/form-data
                if (!req.HasFormContentType)
                {
                    return new BadRequestObjectResult("Request must be 'multipart/form-data' with a PDF file and JSON question(s).");
                }



                // Get the uploaded PDF
                var formData = await req.ReadFormAsync();
                var pdfFile = formData.Files.GetFile("file");
                if (pdfFile == null || pdfFile.Length == 0)
                {
                    return new BadRequestObjectResult("Please upload a PDF file using 'file' as the form field name.");
                }


                // Validate file
                if (formData.Files["file"] is not IFormFile file)
                {
                    return new BadRequestObjectResult("Please upload a PDF file.");
                }


                // Get the question data
                if (!formData.TryGetValue("data", out var questionValues) || string.IsNullOrEmpty(questionValues.FirstOrDefault()))
                {
                    return new BadRequestObjectResult("Please provide 'data' in the form data.");
                }

                string questionPayload = questionValues.FirstOrDefault();
                if (questionPayload.Contains("Glyphosate Matter"))
                {
                    _blobContainerName = "glyphosate-complaintfile";
                }
                else if (questionPayload.Contains("Asbestos NonMidwest Matter"))
                {
                    _blobContainerName = "asbestos-nmidwest-complaintfile";
                }
                else if (questionPayload.Contains("Paraquat Matter"))
                {
                    _blobContainerName = "paraquat-complaintfile";
                }
                else if (questionPayload.Contains("Talc Matter"))
                {
                    _blobContainerName = "talc-complaintfile";
                }

                else
                {
                    return new BadRequestObjectResult("Invalid matter type in the question payload.");
                }
                //call UploadFileAsync
                UploadFileAsync(file, file.FileName, _blobContainerName).Wait();

                // Parse the incoming JSON payload more efficiently
                List<QuestionConfig> questions = await ParseQuestionsAsync(questionPayload);
                if (!questions.Any())
                {
                    return new BadRequestObjectResult("No valid questions provided.");
                }
                //call SummaryPDF method

              //  string summary = await SummaryPDF(file, _pdfViewerModel);




            }
            catch (Exception)
            {

                throw;
            }


            return new OkObjectResult("Welcome to Azure Functions!");
        }

        public async Task<string> UploadFileAsync(IFormFile file, string fileName, string containerName)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("File is empty or null.");

            var blobServiceClient = new BlobServiceClient(_connectionStringBlobStorage);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            // Ensure the container exists
            //  await containerClient.CreateIfNotExistsAsync();

            var blobClient = containerClient.GetBlobClient(fileName);

            using (var stream = new MemoryStream())
            {
                await file.CopyToAsync(stream);
                stream.Position = 0;
                await blobClient.UploadAsync(stream, overwrite: true);
            }

            _logger.LogInformation($"File uploaded to Blob Storage with name: {fileName}");
            return blobClient.Uri.ToString();
        }

        private async Task<List<QuestionConfig>> ParseQuestionsAsync(string questionPayload)
        {
            List<QuestionConfig> questions = new List<QuestionConfig>();

            try
            {
                // Fast path for direct QuestionConfig array
                if (questionPayload.TrimStart().StartsWith("[{"))
                {
                    return JsonConvert.DeserializeObject<List<QuestionConfig>>(questionPayload);
                }

                // Try to parse the nested MatterTypes structure
                if (questionPayload.TrimStart().StartsWith("{"))
                {
                    var matterTypesRequest = JsonConvert.DeserializeObject<MatterTypesRequest>(questionPayload);
                    if (matterTypesRequest?.MatterTypes != null && matterTypesRequest.MatterTypes.Any())
                    {
                        // Combine all questions from all matter types
                        questions = matterTypesRequest.MatterTypes
                            .SelectMany(mt => mt.Value.Questions)
                            .ToList();
                    }
                }

                // Fallback: if it's not JSON in that format, check if it is an array of simple strings
                if (!questions.Any())
                {
                    // If it starts with '[' we assume an array of strings
                    if (questionPayload.TrimStart().StartsWith("["))
                    {
                        var simpleQuestions = JsonConvert.DeserializeObject<List<string>>(questionPayload);
                        // Create default config objects from these simple strings
                        questions = simpleQuestions.Select(q => new QuestionConfig { QuestionText = q }).ToList();
                    }
                    else
                    {
                        // Otherwise, treat it as a raw string
                        questions.Add(new QuestionConfig { QuestionText = questionPayload });
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                _logger.LogInformation("Failed to parse the JSON question; treating the input as a raw string.");
                questions.Add(new QuestionConfig { QuestionText = questionPayload });
            }

            return questions;
        }
        public static async Task<string> SummaryPDF(IFormFile pdfFile, PDFViewerModel pdfViewerModel)
        {
            string systemPrompt = "You are a helpful assistant. Your task is to analyze the provided text and generate short summary.";

            try
            {
                using var stream = pdfFile.OpenReadStream();
                await pdfViewerModel.LoadDocument(stream, "application/pdf");

                var result = await pdfViewerModel.FetchResponseFromAIService(systemPrompt);
                var suggestions = await pdfViewerModel.GetSuggestions();

                if (string.IsNullOrEmpty(result))
                {
                    return "No summary generated.";
                }

                return result;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }

}
