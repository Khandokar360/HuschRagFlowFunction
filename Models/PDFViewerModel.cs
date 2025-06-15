using SmartComponents.LocalEmbeddings;
using Syncfusion.Pdf.Parsing;
using Syncfusion.Drawing;
using HuschRagFlowEngineFunctionApp.Service;
using System.Text;

namespace HuschRagFlowEngineFunctionApp.Models
{
    public class TextBounds
    {
        public string SensitiveInformation { get; set; } = string.Empty;
        public RectangleF Bounds { get; set; }
    }

    public class TreeItem
    {
        public string NodeId { get; set; } = string.Empty;
        public string NodeText { get; set; } = string.Empty;
        public int PageNumber { get; set; }
        public RectangleF Bounds { get; set; }
        public bool? IsChecked { get; set; }
        public bool Expanded { get; set; }
        public List<TreeItem> Child { get; set; } = new();
    }

    public class PDFViewerModel
    {
        public Dictionary<string, EmbeddingF32>? PageEmbeddings { get; private set; }

        private readonly LocalEmbedder _embedder;
        private readonly AzureAIService _azureAIService;

        // Constants
        private const float POINT_TO_PIXEL_RATIO = 96f / 72f;
        private const int DEFAULT_EMBEDDING_RESULTS = 5;
        private const float DEFAULT_SIMILARITY_THRESHOLD = 0.5f;

        public PDFViewerModel(LocalEmbedder embedder, AzureAIService azureAIService)
        {
            _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
            _azureAIService = azureAIService ?? throw new ArgumentNullException(nameof(azureAIService));
        }

        private void CreateEmbeddingChunks(string[] chunks)
        {
            if (chunks?.Length > 0)
            {
                PageEmbeddings = chunks
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk))
                    .Select(x => KeyValuePair.Create(x, _embedder.Embed(x)))
                    .ToDictionary(k => k.Key, v => v.Value);
            }
        }

        public async Task<string> FetchResponseFromAIService(string systemPrompt)
        {
            if (PageEmbeddings == null || !PageEmbeddings.Any())
            {
                return "No content available for analysis.";
            }

            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                throw new ArgumentException("System prompt cannot be null or whitespace.", nameof(systemPrompt));
            }

            var messages = PageEmbeddings.Keys.Take(10).ToList();
            var combinedMessage = string.Join(" ", messages);

            var result = await _azureAIService.GetCompletionAsync(combinedMessage, false, false, systemPrompt);
            return result;
        }

        /// <summary>
        /// Load the document and extract text page by page
        /// </summary>
        /// <param name="stream">Document stream</param>
        /// <param name="mimeType">MIME type of the document</param>
        /// <returns>List of extracted text</returns>
        public async Task<List<string>> LoadDocument(Stream stream, string mimeType)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var extractedText = new List<string>();

            if (!mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Only PDF documents are supported.", nameof(mimeType));
            }

            try
            {
                using var loadedDocument = new PdfLoadedDocument(stream);
                var loadedPages = loadedDocument.Pages;

                // Extract annotations
                await ExtractAnnotations(loadedDocument, extractedText);

                // Extract form fields
                await ExtractFormFields(loadedDocument, extractedText);

                // Extract text from pages
                ExtractPageText(loadedPages, extractedText);

                // Create embeddings if we have an embedder
                CreateEmbeddingChunks(extractedText.ToArray());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load PDF document: {ex.Message}", ex);
            }

            return extractedText;
        }

        private static async Task ExtractAnnotations(PdfLoadedDocument loadedDocument, List<string> extractedText)
        {
            try
            {
                using var annotationStream = new MemoryStream();
                loadedDocument.ExportAnnotations(annotationStream, AnnotationDataFormat.Json);
                string annotations = ConvertToString(annotationStream);

                if (!string.IsNullOrWhiteSpace(annotations))
                {
                    extractedText.Add($"Annotations: {annotations}");
                }
            }
            catch (Exception)
            {
                // Annotations extraction failed, continue without them
            }
        }

        private static async Task ExtractFormFields(PdfLoadedDocument loadedDocument, List<string> extractedText)
        {
            try
            {
                if (loadedDocument.Form != null)
                {
                    using var formStream = new MemoryStream();
                    loadedDocument.Form.ExportData(formStream, DataFormat.Json, "form");
                    string formFields = ConvertToString(formStream);

                    if (!string.IsNullOrWhiteSpace(formFields))
                    {
                        extractedText.Add($"Form fields: {formFields}");
                    }
                }
            }
            catch (Exception)
            {
                // Form fields extraction failed, continue without them
            }
        }

        private static void ExtractPageText(PdfLoadedPageCollection loadedPages, List<string> extractedText)
        {
            for (int i = 0; i < loadedPages.Count; i++)
            {
                try
                {
                    var pageText = loadedPages[i].ExtractText();
                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        extractedText.Add($"... Page {i + 1} ...\n{pageText}");
                    }
                }
                catch (Exception)
                {
                    // Page text extraction failed, continue with next page
                    extractedText.Add($"... Page {i + 1} ...\n[Text extraction failed]");
                }
            }
        }

        private static string ConvertToString(MemoryStream memoryStream)
        {
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Get the Text Bounds of the text to be searched
        /// </summary>
        public Dictionary<int, List<TextBounds>> FindTextBounds(Stream stream, List<string> sensitiveInformations)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (sensitiveInformations == null)
                throw new ArgumentNullException(nameof(sensitiveInformations));

            var accumulatedBounds = new Dictionary<int, List<TextBounds>>();

            using var loadedDocument = new PdfLoadedDocument(stream);

            foreach (var info in sensitiveInformations.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                try
                {
                    loadedDocument.FindText(info, out Dictionary<int, List<RectangleF>> bounds);
                    MergeBounds(accumulatedBounds, bounds, info);
                }
                catch (Exception)
                {
                    // Continue with other sensitive information if one fails
                }
            }

            ProcessBounds(accumulatedBounds);
            return accumulatedBounds;
        }

        private static void MergeBounds(Dictionary<int, List<TextBounds>> accumulatedBounds,
            Dictionary<int, List<RectangleF>> bounds, string sensitiveInfo)
        {
            foreach (var pair in bounds)
            {
                if (!accumulatedBounds.ContainsKey(pair.Key))
                {
                    accumulatedBounds[pair.Key] = new List<TextBounds>();
                }

                accumulatedBounds[pair.Key].AddRange(
                    pair.Value.Select(rect => new TextBounds
                    {
                        SensitiveInformation = sensitiveInfo,
                        Bounds = rect
                    }));
            }
        }

        private static void ProcessBounds(Dictionary<int, List<TextBounds>> accumulatedBounds)
        {
            foreach (var pair in accumulatedBounds)
            {
                var maxWidthBounds = new Dictionary<(float X, float Y), TextBounds>();

                foreach (var textBound in pair.Value)
                {
                    var rect = textBound.Bounds;
                    rect.X = ConvertPointToPixel(rect.X) - 2;
                    rect.Y = ConvertPointToPixel(rect.Y) - 2;
                    rect.Height = ConvertPointToPixel(rect.Height) + 2;
                    rect.Width = ConvertPointToPixel(rect.Width) + 2;

                    var key = (rect.X, rect.Y);

                    if (!maxWidthBounds.TryGetValue(key, out var existingTextBound) ||
                        rect.Width > existingTextBound.Bounds.Width)
                    {
                        maxWidthBounds[key] = new TextBounds
                        {
                            SensitiveInformation = textBound.SensitiveInformation,
                            Bounds = rect
                        };
                    }
                }

                pair.Value.Clear();
                pair.Value.AddRange(maxWidthBounds.Values);
            }
        }

        private static float ConvertPointToPixel(float number)
        {
            return number * POINT_TO_PIXEL_RATIO;
        }

        /// <summary>
        /// Get the Sensitive Data with the selected patterns from the PDF using OpenAI 
        /// </summary>
        public async Task<List<string>> GetSensitiveDataFromPDF(string text, List<string> selectedItems)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            if (selectedItems == null || !selectedItems.Any())
                return new List<string>();

            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("I have a block of text containing various pieces of information. Please help me identify and extract any Personally Identifiable Information (PII) present in the text. The PII categories I am interested in are:");

            foreach (var item in selectedItems.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                stringBuilder.AppendLine(item);
            }

            stringBuilder.AppendLine("Please provide the extracted information as a plain list, separated by commas, without any prefix or numbering or extra content.");

            string prompt = stringBuilder.ToString();
            var answer = await FetchResponseFromAIService(prompt);

            if (string.IsNullOrWhiteSpace(answer))
                return new List<string>();

            var output = answer.Trim();
            var namesSet = new HashSet<string>(
                output.Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => name.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            return namesSet.ToList();
        }

        public async Task<string> AnswerQuestion(string userPrompt)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
                throw new ArgumentException("User prompt cannot be null or whitespace.", nameof(userPrompt));

            if (PageEmbeddings == null || !PageEmbeddings.Any())
                return "No document content available to answer questions.";

            var questionEmbedding = _embedder.Embed(userPrompt);
            var results = LocalEmbedder.FindClosestWithScore(questionEmbedding,
                PageEmbeddings.Select(x => (x.Key, x.Value)),
                DEFAULT_EMBEDDING_RESULTS,
                DEFAULT_SIMILARITY_THRESHOLD);

            if (!results.Any())
                return "No relevant content found to answer your question.";

            var relevantContent = new StringBuilder();
            foreach (var result in results)
            {
                relevantContent.AppendLine(result.Item);
            }

            string message = relevantContent.ToString();
            string systemPrompt = $"You are a helpful assistant. Use the provided PDF document pages and pick a precise page to answer the user question, provide a reference at the bottom of the content with page numbers like ex: Reference: [20,21,23]. Pages: {message}";

            var answer = await _azureAIService.GetCompletionAsync(userPrompt, false, false, systemPrompt);
            return answer;
        }

        /// <summary>
        /// Get the answer for the question using GPT-4o and local embeddings
        /// </summary>
        /// <param name="question">The question to answer</param>
        /// <returns>Answer with suggestions</returns>
        public async Task<string> GetAnswer(string question)
        {
            if (string.IsNullOrWhiteSpace(question))
                throw new ArgumentException("Question cannot be null or whitespace.", nameof(question));

            try
            {
                var answer = await AnswerQuestion(question);
                var suggestions = await GetSuggestions();
                return $"{answer}\n\nSuggestions:\n{suggestions}";
            }
            catch (Exception ex)
            {
                return $"Error getting answer: {ex.Message}";
            }
        }

        /// <summary>
        /// Get the suggestions using GPT-4o and local embeddings
        /// </summary>
        /// <returns>Generated suggestions</returns>
        public async Task<string> GetSuggestions()
        {
            try
            {
                return await FetchResponseFromAIService("You are a helpful assistant. Your task is to analyze the provided text and generate 3 short diverse questions and each question should not exceed 10 words");
            }
            catch (Exception ex)
            {
                return $"Error generating suggestions: {ex.Message}";
            }
        }
    }
}