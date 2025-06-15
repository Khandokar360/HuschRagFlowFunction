using Microsoft.Extensions.AI;
using SmartComponents.LocalEmbeddings;
using Syncfusion.Blazor.SmartComponents;
using System.Text;

namespace HuschRagFlowEngineFunctionApp.Models
{
    public class DocumentQA
    {
        public Dictionary<string, EmbeddingF32>? PageEmbeddings { get; private set; }

        private readonly List<string> _extractedText = new();
        private string DocumentContent { get; set; } = string.Empty;
        private readonly LocalEmbedder _embedder;
        private readonly OpenAIConfiguration _openAIService;

        // Constants
        private const int DEFAULT_CHUNK_SIZE = 4000;
        private const int DEFAULT_TOP_RESULTS = 2;

        public DocumentQA(LocalEmbedder embedder, OpenAIConfiguration azureAIService)
        {
            _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
            _openAIService = azureAIService ?? throw new ArgumentNullException(nameof(azureAIService));
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

        /// <summary>
        /// Get answer from GPT-4o
        /// </summary>
        /// <param name="systemPrompt">System prompt for the AI</param>
        /// <param name="message">User message</param>
        /// <param name="isSummary">Whether this is for summary generation</param>
        /// <returns>AI response</returns>
        public async Task<string> GetAnswerFromGPT(string systemPrompt, string message, bool isSummary = false)
        {
            if (string.IsNullOrWhiteSpace(systemPrompt))
                throw new ArgumentException("System prompt cannot be null or whitespace.", nameof(systemPrompt));

            if (PageEmbeddings == null || !PageEmbeddings.Any())
                return "No content available for analysis.";

            var chatParameters = new ChatParameters
            {
                Messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt)
                }
            };

            try
            {
                if (isSummary)
                {
                    return await GenerateSummary(chatParameters);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(message))
                        throw new ArgumentException("Message cannot be null or whitespace when not generating summary.", nameof(message));

                    chatParameters.Messages.Add(new ChatMessage(ChatRole.User, message));
                    var result = await _openAIService.GetChatResponseAsync(chatParameters);
                    return result?.ToString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                return $"Error getting GPT response: {ex.Message}";
            }
        }

        private async Task<string> GenerateSummary(ChatParameters chatParameters)
        {
            var summaryResults = new List<string>();
            var chunks = PageEmbeddings.Keys.ToList();

            foreach (var chunk in chunks)
            {
                try
                {
                    chatParameters.Messages.Add(new ChatMessage(ChatRole.User, chunk));
                    var result = await _openAIService.GetChatResponseAsync(chatParameters);

                    if (result != null && !string.IsNullOrWhiteSpace(result.ToString()))
                    {
                        summaryResults.Add(result.ToString());
                    }

                    // Remove the user message for next iteration
                    chatParameters.Messages.RemoveAt(chatParameters.Messages.Count - 1);
                }
                catch (Exception)
                {
                    // Continue with next chunk if one fails
                    if (chatParameters.Messages.Count > 1)
                    {
                        chatParameters.Messages.RemoveAt(chatParameters.Messages.Count - 1);
                    }
                }
            }

            return summaryResults.Any() ? string.Join(" ", summaryResults) : "No summary could be generated.";
        }

        public async Task LoadDocument(string document)
        {
            if (string.IsNullOrWhiteSpace(document))
                throw new ArgumentException("Document content cannot be null or whitespace.", nameof(document));

            _extractedText.Clear();
            DocumentContent = document;

            var chunks = ChunkDocument(document, DEFAULT_CHUNK_SIZE);
            _extractedText.AddRange(chunks);

            CreateEmbeddingChunks(_extractedText.ToArray());
        }

        private static List<string> ChunkDocument(string document, int chunkSize)
        {
            var chunks = new List<string>();
            int start = 0;

            while (start < document.Length)
            {
                int length = Math.Min(chunkSize, document.Length - start);

                // Try to find a sentence boundary
                int lastPeriod = document.LastIndexOf('.', start + length - 1, length);

                if (lastPeriod > start)
                {
                    string chunk = document.Substring(start, lastPeriod - start + 1);
                    chunks.Add(chunk.Trim());
                    start = lastPeriod + 1;
                }
                else
                {
                    string chunk = document.Substring(start, length);
                    chunks.Add(chunk.Trim());
                    start += length;
                }
            }

            return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        }

        public async Task<string> GetDocumentSummary()
        {
            const string systemPrompt = "You are a helpful assistant. Your task is to analyze the provided text and generate short summary. Always respond in proper HTML format, but do not include <html>, <head>, or <body> tags.";

            try
            {
                return await GetAnswerFromGPT(systemPrompt, string.Empty, true);
            }
            catch (Exception ex)
            {
                return $"Error generating document summary: {ex.Message}";
            }
        }

        /// <summary>
        /// Find closest page embedding and answer the question using GPT-4o
        /// </summary>
        /// <param name="systemPrompt">System prompt for context</param>
        /// <param name="question">User question</param>
        /// <returns>AI response</returns>
        public async Task<string> GetAnswer(string systemPrompt, string question)
        {
            if (string.IsNullOrWhiteSpace(question))
                throw new ArgumentException("Question cannot be null or whitespace.", nameof(question));

            if (PageEmbeddings == null || !PageEmbeddings.Any())
                return "No document content available to answer questions.";

            try
            {
                var questionEmbedding = _embedder.Embed(question);
                var results = LocalEmbedder.FindClosest(questionEmbedding,
                    PageEmbeddings.Select(x => (x.Key, x.Value)),
                    DEFAULT_TOP_RESULTS);

                if (!results.Any())
                    return "No relevant content found to answer your question.";

                var contextualPrompt = systemPrompt + " Context: " + string.Join(" --- ", results);
                var answer = await GetAnswerFromGPT(contextualPrompt, question);

                return answer;
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
            const string prompt = "You are a helpful assistant. Your task is to analyze the provided text and generate 3 short diverse questions and each question should not exceed 10 words";

            try
            {
                return await GetAnswerFromGPT(prompt, DocumentContent);
            }
            catch (Exception ex)
            {
                return $"Error generating suggestions: {ex.Message}";
            }
        }

        /// <summary>
        /// Get the number of chunks/embeddings created
        /// </summary>
        public int GetChunkCount()
        {
            return PageEmbeddings?.Count ?? 0;
        }

        /// <summary>
        /// Clear all loaded document data
        /// </summary>
        public void ClearDocument()
        {
            _extractedText.Clear();
            DocumentContent = string.Empty;
            PageEmbeddings = null;
        }
    }
}